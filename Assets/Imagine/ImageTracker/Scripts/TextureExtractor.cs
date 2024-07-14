using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;


namespace Imagine.WebAR
{
    public class TextureExtractor : MonoBehaviour
    {

        private Texture2D dataUrlTexture;
        [SerializeField] private MeshRenderer meshRenderer;

        [SerializeField] private string id;
        [SerializeField] private float delay = 0.2f;

        [DllImport("__Internal")] private static extern string GetWebGLWarpedTexture(string id);

        //[SerializeField] [TextArea(5,10)] public string testData = "";


        private float lastExtractTime = -1;

        // Start is called before the first frame update
        void Start()
        {
            dataUrlTexture = new Texture2D(512, 512);

            if(meshRenderer == null){
                meshRenderer = GetComponent<MeshRenderer>();
            }
            meshRenderer.material.mainTexture = dataUrlTexture;
        }

    // Update is called once per frame
        void Update()
        {
            if(Time.time - lastExtractTime > delay){
                lastExtractTime = Time.time;

                var dataUrlStr = "";
                //if(string.IsNullOrEmpty(testData)){
                    dataUrlStr = GetWebGLWarpedTexture(this.id);
                // }
                // else{
                //     dataUrlStr = testData;
                // }
                dataUrlStr = dataUrlStr.Replace("data:image/jpeg;base64,", "");
                dataUrlTexture.LoadImage(System.Convert.FromBase64String(dataUrlStr));
                dataUrlTexture.Apply();

            }
        }

 
    }
}

