using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace TSW
{
    public class MeshFragmenting
    {
        public int Count => _fragmentDataArray.Length;
        public FragmentData this[int index] {
            get {
                return _fragmentDataArray[index];
            }
        }

        public NativeArray<FragmentData> FragmentDataArray {
            get { return _fragmentDataArray; }
        }

        public struct FragmentData
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
            public Vector3 p0;
            public Vector3 p1;
            public Vector3 p2;

            public Vector3 this[int index] {
                get {
                    switch (index) {
                        case 0: return p0;
                        case 1: return p1;
                        case 2: return p2;
                    }
                    throw new System.IndexOutOfRangeException();
                }
            }

            public float Area {
                get {
                    var v1 = p2 - p0;
                    var v2 = p2 - p1;
                    return Vector3.Cross(v1, v2).magnitude * .5f;
                }
            }
        }

        public Mesh Initialize(Mesh _sourceMesh) {
            _initialized = false;

            var triangles = _sourceMesh.triangles;
            var sourceVertices = _sourceMesh.vertices;
            if (triangles.Length == sourceVertices.Length) {
                Debug.Log("mesh already split");
                return null;
            }

            var vertexCount = triangles.Length;
            var subMeshCount = _sourceMesh.subMeshCount;

            _mesh = new Mesh();
            _mesh.name = "FragmentDynamicMesh";

            _meshVertices = new NativeArray<Vertex>(vertexCount, Allocator.Persistent);
            _meshIndices = new NativeArray<ushort>(vertexCount, Allocator.Persistent);
            _fragmentDataArray = new NativeArray<FragmentData>(vertexCount / 3, Allocator.Persistent);
            _subMeshInfoArray = new SubMeshInfo[subMeshCount];
            _subMeshCount = _sourceMesh.subMeshCount;

            var sourceUV = _sourceMesh.uv;
            int vertexIndex = 0;
            for (int meshIdx = 0; meshIdx < _sourceMesh.subMeshCount; ++meshIdx) {
                var sourceTriangles = _sourceMesh.GetTriangles(meshIdx);
                SubMeshInfo subMeshInfo;
                subMeshInfo.startIndex = vertexIndex;

                for (int i = 0; i < sourceTriangles.Length; i++) {
                    ushort idx = (ushort)sourceTriangles[i];
                    _meshVertices[vertexIndex] = new Vertex { pos = sourceVertices[idx], uv = sourceUV[idx] };
                    _meshIndices[vertexIndex] = (ushort)vertexIndex;
                    vertexIndex++;
                }
                subMeshInfo.indexCount = vertexIndex - subMeshInfo.startIndex;
                _subMeshInfoArray[meshIdx] = subMeshInfo;
            }

            for (int i = 0; i < _meshVertices.Length; i += 3) {
                var v1 = _meshVertices[i];
                var v2 = _meshVertices[i + 1];
                var v3 = _meshVertices[i + 2];
                var centroid = (v1.pos + v2.pos + v3.pos) / 3f;
                var rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Cross(v1.pos - v2.pos, v1.pos - v3.pos));
                var fragToMesh = Matrix4x4.TRS(centroid, rotation, Vector3.one);
                var meshToFrag = fragToMesh.inverse;
                var fragment = _fragmentDataArray[i / 3];
                fragment.position = centroid;
                fragment.rotation = rotation;
                fragment.scale = Vector3.one;
                fragment.p0 = meshToFrag.MultiplyPoint(v1.pos);
                fragment.p1 = meshToFrag.MultiplyPoint(v2.pos);
                fragment.p2 = meshToFrag.MultiplyPoint(v3.pos);
                _fragmentDataArray[i / 3] = fragment;
            }

            UpdateMesh();

            _initialized = true;
            return _mesh;
        }

        public void ScheduleCopyToVertice(JobHandle fragUpdateJobHandle) {
            var fragToVertice = new FragToVerticeJob();
            fragToVertice.vertices = _meshVertices;
            fragToVertice.fragments = _fragmentDataArray;
            _handle = fragToVertice.Schedule(_meshVertices.Length, 64, fragUpdateJobHandle);
            _scheduled = true;
        }

        public void Complete() {
            if (!_scheduled) {
                return;
            }

            _handle.Complete();

            UpdateMesh();
        }

        public void Dispose() {
            if (_meshVertices.IsCreated)
                _meshVertices.Dispose();
            if (_meshIndices.IsCreated)
                _meshIndices.Dispose();
            if (_fragmentDataArray.IsCreated)
                _fragmentDataArray.Dispose();
            if (_mesh)
                Object.Destroy(_mesh);
        }

        private static VertexAttributeDescriptor[] layout = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
        };

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 pos;
            public Vector2 uv;
        }

        private struct SubMeshInfo
        {
            public int startIndex;
            public int indexCount;
        };

        private struct FragToVerticeJob : IJobParallelFor
        {
            public NativeArray<Vertex> vertices;
            [ReadOnly] public NativeArray<FragmentData> fragments;

            public void Execute(int index) {
                int fragIndex = Mathf.FloorToInt(index / 3f);
                FragmentData frag = fragments[fragIndex];

                Matrix4x4 fragToMesh = Matrix4x4.TRS(frag.position, frag.rotation, frag.scale);
                Vertex vertex = vertices[index];
                vertex.pos = fragToMesh.MultiplyPoint(frag[index % 3]);
                vertices[index] = vertex;
            }
        }

        private Mesh _mesh;
        private Mesh _sourceMesh;
        private NativeArray<Vertex> _meshVertices;
        private NativeArray<ushort> _meshIndices;
        private NativeArray<FragmentData> _fragmentDataArray;
        private SubMeshInfo[] _subMeshInfoArray;
        private int _subMeshCount;
        private JobHandle _handle;
        private bool _initialized = false;
        private bool _scheduled = false;

        private void UpdateMesh() {
            if (!_initialized)
                return;

            var vertexCount = _meshVertices.Length;
            _mesh.subMeshCount = _subMeshCount;
            _mesh.SetVertexBufferParams(vertexCount, layout);
            _mesh.SetVertexBufferData(_meshVertices, 0, 0, vertexCount);
            _mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt16);

            _mesh.SetIndexBufferData(_meshIndices, 0, 0, vertexCount);

            for (int meshIdx = 0; meshIdx < _subMeshCount; ++meshIdx) {
                _mesh.SetSubMesh(meshIdx, new SubMeshDescriptor(_subMeshInfoArray[meshIdx].startIndex, _subMeshInfoArray[meshIdx].indexCount, MeshTopology.Triangles));
            }

            _mesh.RecalculateBounds();
            _mesh.RecalculateNormals();
        }
    }
}