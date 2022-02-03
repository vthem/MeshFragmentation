using UnityEngine;

public class FragmentKeySpace : MonoBehaviour
{
	// Update is called once per frame
	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.Space))
		{
			GetComponent<TSW.MeshExplosion>().Initialize();
		}
	}
}
