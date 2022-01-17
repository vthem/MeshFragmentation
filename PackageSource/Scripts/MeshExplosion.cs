using Unity.Collections;
using Unity.Jobs;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace TSW
{
    public class MeshExplosion : MonoBehaviour
    {
        public Vector3 spreadDirection;
        public float gravity = 9.81f;
        public float spreadforce = 1f;
        public float duration = 5f;
        public float drag = 3f;        
        public float minAngularVelocity;
        public float maxAngularVelocity;
        public Spreading[] spreadings = new Spreading[0];
        public bool destroyOnEnd = false;

        [System.Serializable]
        public struct Spreading
        {
            public float quantity;
            public float minConeRadius;
            public float maxConeRadius;
            public float coneRingDistance;
            public float minForce;
            public float maxForce;
            public Color debugColor;
            public float minAngle;
            public float maxAngle;

            public Vector3 GetRandomDirection(Vector3 mainDirection) {
                Vector3 randomInCircleRing = (Quaternion.AngleAxis(Random.Range(minAngle, maxAngle), Vector3.forward) * Vector3.right) * Random.Range(minConeRadius, maxConeRadius);
                Quaternion rot = Quaternion.FromToRotation(Vector3.forward, mainDirection);
                return ((rot * randomInCircleRing) + mainDirection * coneRingDistance).normalized;
            }

            public static Spreading Default {
                get {
                    Spreading s;
                    s.quantity = 1f;
                    s.minConeRadius = 0f;
                    s.maxConeRadius = 1f;
                    s.coneRingDistance = 1f;
                    s.minForce = 1f;
                    s.maxForce = 1f;
                    s.minAngle = 0f;
                    s.maxAngle = 180f;
                    s.debugColor = Color.red;
                    return s;
                }
            }

            private static float Remap(float v, float min, float max) {
                return min + v * (max - min);
            }
        }

        public void Initialize() {
            if (_initialized) {
                DisposeNativeArray();
                _meshFragmenting.Dispose();
            }
            else {
                _originalMesh = GetComponent<MeshFilter>().sharedMesh;
            }
            _initialized = false;

            var fragmentedMesh = _meshFragmenting.Initialize(_originalMesh);
            if (!fragmentedMesh)
                return;

            int fragCount = _meshFragmenting.Count;
            _fragmentExplosionDataArray = new NativeArray<FragmentExplosionData>(fragCount, Allocator.Persistent);

            _jobRemainingTime = duration;
            var localSpreadDirection = transform.InverseTransformDirection(spreadDirection);
            SpreadingIterator spreadingIterator = new SpreadingIterator(spreadings, fragCount);
            _maxArea = 0f;
            for (int i = 0; i < fragCount; ++i) {
                var spreading = spreadingIterator.Next();
                var frag = _meshFragmenting[i];
                var explosion = _fragmentExplosionDataArray[i];
                explosion.velocity = spreading.GetRandomDirection(localSpreadDirection) * spreadforce * Random.Range(spreading.minForce, spreading.maxForce);
                _maxArea = Mathf.Max(_maxArea, frag.Area);
                explosion.angularVelocity = new Vector3(
                    Random.Range(minAngularVelocity, maxAngularVelocity),
                    Random.Range(minAngularVelocity, maxAngularVelocity),
                    Random.Range(minAngularVelocity, maxAngularVelocity));
                _fragmentExplosionDataArray[i] = explosion;
            }

            GetComponent<MeshFilter>().mesh = fragmentedMesh;

            _initialized = true;
        }

        private struct SpreadingIterator
        {
            public SpreadingIterator(Spreading[] spreadings, int maxIteration) {
                index = 0;
                iteration = 0;
                this.maxIteration = maxIteration;
                if (spreadings.Length == 0) {
                    spreadings = new Spreading[1];
                    spreadings[0] = Spreading.Default;
                }
                this.spreadings = spreadings;
            }

            public Spreading Next() {
                iteration++;
                var spreading = spreadings[index];
                if (iteration / (float)maxIteration > spreading.quantity) {
                    index = Mathf.Min(index + 1, spreadings.Length - 1);
                    spreading = spreadings[index];
                }
                return spreading;
            }

            private Spreading[] spreadings;
            private int index;
            private int iteration;
            private int maxIteration;
        }

        private NativeArray<FragmentExplosionData> _fragmentExplosionDataArray;
        private float _jobRemainingTime;
        private MeshFragmenting _meshFragmenting = new MeshFragmenting();
        private bool _initialized = false;
        private Mesh _originalMesh = null;
        private float _maxArea = 0f;

        private struct FragmentExplosionData
        {
            public Vector3 velocity;
            public Vector3 angularVelocity;
        }

        private struct FragmentExplosionJob : IJobParallelFor
        {
            public NativeArray<MeshFragmenting.FragmentData> fragDataArray;
            public NativeArray<FragmentExplosionData> fragExplosionDataArray;
            [ReadOnly] public float deltaTime;
            [ReadOnly] public float gravity;
            [ReadOnly] public float duration;
            [ReadOnly] public float remainingTime;
            [ReadOnly] public float drag;
            [ReadOnly] public float maxAera;

            public void Execute(int index) {
                MeshFragmenting.FragmentData frag = fragDataArray[index];
                FragmentExplosionData explosion = fragExplosionDataArray[index];
                var dir = explosion.velocity.normalized;
                explosion.velocity += Vector3.down * gravity * deltaTime;
                var fragDrag = drag * frag.Area / maxAera;
                explosion.velocity -= dir * fragDrag * deltaTime;

                frag.position += explosion.velocity * deltaTime;
                frag.rotation *= Quaternion.Euler(explosion.angularVelocity * deltaTime);
                frag.rotation.Normalize();
                frag.scale -= Vector3.one * (deltaTime / duration);
                fragDataArray[index] = frag;
            }
        }

        private void DisposeNativeArray() {

            if (_fragmentExplosionDataArray.IsCreated)
                _fragmentExplosionDataArray.Dispose();
        }

        private void ScheduleJob() {
            var explosionJob = new FragmentExplosionJob();
            explosionJob.fragDataArray = _meshFragmenting.FragmentDataArray;
            explosionJob.fragExplosionDataArray = _fragmentExplosionDataArray;
            explosionJob.deltaTime = Time.deltaTime;
            explosionJob.duration = duration;
            explosionJob.remainingTime = _jobRemainingTime;
            explosionJob.gravity = gravity;
            explosionJob.maxAera = _maxArea;
            explosionJob.drag = drag;

            var explosionHandle = explosionJob.Schedule(_meshFragmenting.Count, 64);

            _meshFragmenting.ScheduleCopyToVertice(explosionHandle);
        }

        private void Update() {
            _jobRemainingTime -= Time.deltaTime;
            if (_initialized && _jobRemainingTime <= 0f && destroyOnEnd)
                GameObject.Destroy(gameObject);
            if (!_initialized || _jobRemainingTime <= 0f)
                return;            
            ScheduleJob();
        }

        private void LateUpdate() {

            _meshFragmenting.Complete();
        }

        private void OnDrawGizmosSelected() {
            Random.InitState(0);
            SpreadingIterator spreadingIterator = new SpreadingIterator(spreadings, 100);

            for (int i = 0; i < 100; ++i) {
                var spreading = spreadingIterator.Next();
                var dir = spreading.GetRandomDirection(spreadDirection);
                Gizmos.color = spreading.debugColor;
                Gizmos.DrawLine(transform.position, transform.position + dir * 10);
            }
            Gizmos.color = Color.white;
        }

        private void OnDestroy() {
            if (_initialized) {
                DisposeNativeArray();
                _meshFragmenting.Dispose();
            }
        }

    }

#if UNITY_EDITOR
    [CustomEditor(typeof(MeshExplosion))]
    public class MeshExplosionEditor : Editor
    {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();

            if (EditorApplication.isPlaying) {
                var explosion = target as MeshExplosion;
                if (GUILayout.Button("Fragment")) {
                    explosion.Initialize();
                }
            }
        }
    }
#endif

}