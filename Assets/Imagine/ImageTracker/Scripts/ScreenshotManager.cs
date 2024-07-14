using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;


namespace Imagine.WebAR
{
    public class ScreenshotManager : MonoBehaviour
    {
        [DllImport("__Internal")] private static extern string GetWebGLVideoDims();
        [DllImport("__Internal")] private static extern string GetWebGLCameraFrame(string typeStr);

        [DllImport("__Internal")] private static extern void ShowWebGLScreenshot(string dataUrl);

        [SerializeField] private AudioClip shutterSound;
        [SerializeField] private AudioSource shutterSoundSource;
        public Texture2D screenShot;
        Texture2D dataUrlTexture;

        public void GetScreenShot_Experimental(){
            Debug.Log("GetScreenshot");

#if !UNITY_EDITOR
            if(dataUrlTexture == null){
                var dims = GetWebGLVideoDims();
                Debug.Log(dims);
                var vals = dims.Split(new string[] { "," }, System.StringSplitOptions.RemoveEmptyEntries);
                var w = int.Parse(vals[0]);
                var h = int.Parse(vals[1]);
                dataUrlTexture = new Texture2D(w, h);
            }
            //get camera image from javascript
            GetCameraFrame();
            Debug.Log("Got Camera Image");
#endif

            // Create a RenderTexture to temporarily hold the camera image
            Texture2D overlayTexture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
            RenderTexture renderTexture = new RenderTexture(overlayTexture.width, overlayTexture.height, 24);
            Camera.main.targetTexture = renderTexture;
            Camera.main.Render();

            // Read the pixels from the RenderTexture into the Texture2D
            RenderTexture.active = renderTexture;
            overlayTexture.ReadPixels(new Rect(0, 0, overlayTexture.width, overlayTexture.height), 0, 0);
            overlayTexture.Apply();

            // Clean up by resetting the targetTexture and releasing the RenderTexture
            Camera.main.targetTexture = null;
            RenderTexture.active = null;
            Destroy(renderTexture);

            

#if UNITY_EDITOR
            Texture2D baseTexture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
            TextureScale.Bilinear(baseTexture, overlayTexture.width, overlayTexture.height);
            Color[] basePixels = baseTexture.GetPixels();
#else
            //compute crop rect based on aspect
            var ar = (float)Screen.width/Screen.height;
            var videoW = dataUrlTexture.width;
            var videoH = dataUrlTexture.height;
            var v_ar = (float)videoW/videoH;

            Rect videoROI = new Rect();

            if(ar < 1){ //portrait - bleed horizontally
                var newW = videoH * ar;
                videoROI.x = (videoW - newW)/2;
                videoROI.y = 0;
                videoROI.width = newW;
                videoROI.height = videoH;
                Debug.Log(videoW + "x" + videoH + "(" + v_ar + ") => " + newW + "x" + videoH + "(" + ar + ")");
            }
            else{ //landscape - bleed vertically
                var newH = videoW / ar;
                videoROI.x = 0;
                videoROI.y = (videoH - newH)/2;
                videoROI.width = videoW;
                videoROI.height = newH;
                Debug.Log(videoW + "x" + videoH + "(" + v_ar + ") => " + videoW + "x" + newH + "(" + ar + ")");
            }
            
            //crop video
            dataUrlTexture = CropTexture(dataUrlTexture, videoROI);

            TextureScale.Bilinear(dataUrlTexture, overlayTexture.width, overlayTexture.height);
            //TODO: Crop dataUrlTexture to aspect ratio
            Color[] basePixels = dataUrlTexture.GetPixels();
#endif

            Color[] overlayPixels = overlayTexture.GetPixels();
            Color[] mergedPixels = new Color[basePixels.Length];

            for (int i = 0; i < basePixels.Length; i++)
            {
                Color baseColor = basePixels[i];
                Color overlayColor = overlayPixels[i];

                // Merge the colors based on the overlay's alpha
                Color mergedColor = baseColor * (1 - overlayColor.a) + overlayColor * overlayColor.a;
                mergedPixels[i] = mergedColor;
            }

            Texture2D mergedTexture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
            mergedTexture.SetPixels(mergedPixels);
            mergedTexture.Apply();

            screenShot = mergedTexture;

            if(shutterSoundSource != null && shutterSound != null){
                shutterSoundSource.PlayOneShot(shutterSound);
            }

            //GetComponent<TextureDownloader>().DownloadTexture(mergedTexture);
#if UNITY_EDITOR
            Debug.Log("Screenshots are displayed only in WebGL builds");
#else
            byte[] textureBytes = mergedTexture.EncodeToJPG();
            string dataUrlStr = "data:image/jpeg;base64," + System.Convert.ToBase64String(textureBytes);
            ShowWebGLScreenshot(dataUrlStr);
#endif
        }

        void GetCameraFrame()
        {
            var dataUrlStr = GetWebGLCameraFrame("image/jpeg");
            dataUrlStr = dataUrlStr.Replace("data:image/jpeg;base64,", "");
            dataUrlTexture.LoadImage(System.Convert.FromBase64String(dataUrlStr));
            dataUrlTexture.Apply();
        }

        private Texture2D CropTexture(Texture2D sourceTexture, Rect cropRect)
        {
            // Create a new texture for the cropped region
            Texture2D croppedTexture = new Texture2D((int)cropRect.width, (int)cropRect.height);

            // Loop through each pixel in the cropped region
            for (int y = 0; y < cropRect.height; y++)
            {
                for (int x = 0; x < cropRect.width; x++)
                {
                    // Get the pixel color from the source texture
                    Color pixelColor = sourceTexture.GetPixel((int)cropRect.x + x, (int)cropRect.y + y);

                    // Set the pixel color in the cropped texture
                    croppedTexture.SetPixel(x, y, pixelColor);
                }
            }

            // Apply changes and update the cropped texture
            croppedTexture.Apply();

            return croppedTexture;
        }
    }
}

