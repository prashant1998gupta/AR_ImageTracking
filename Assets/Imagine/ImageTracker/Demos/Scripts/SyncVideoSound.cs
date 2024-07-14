using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace Imagine.WebAR.Samples
{
    public class SyncVideoSound : MonoBehaviour
    {
        [SerializeField] VideoPlayer video;
        [SerializeField] AudioSource sound;

        public float lastPos = 0;

        void Awake(){
            
        }
        void OnEnable(){
            StartCoroutine("SyncRoutine");
        }

        void OnDisable(){
            StopCoroutine("SyncRoutine");
        }
        
        IEnumerator SyncRoutine()
        {
            var videoRenderer = video.GetComponent<Renderer>();
            videoRenderer.enabled = false;

            while(!video.isPrepared){
                Debug.Log("Waiting video preparation");
                yield return null;
            }

            video.Play();
            sound.Play();

            video.time = lastPos;
            //sound.time = lastPos;

            while(true){
                if(video.time > 0.01f)
                {
                    videoRenderer.enabled = true;
                }
                else if(!sound.isPlaying){
                    sound.time = (float)video.time;
                    sound.Play();

                }
                    

                if(Mathf.Abs(sound.time - (float)video.time) > 0.1){
                    Debug.Log(sound.time + ", " + sound.clip.length);
                    
                    sound.time = (float)video.time;
                    Debug.Log(sound.time + "=>" + video.time);
                }
               

                lastPos = (float)video.time;
                //yield return new WaitForSeconds(0.05f);
                yield return null;
            }
        }

        
    }
}
