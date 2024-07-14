using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using UnityEngine;

namespace Imagine.WebAR.Samples
{
    public class GoToUrl : MonoBehaviour
    {
        [DllImport("__Internal")] private static extern void ShowConfirmUrl(string url);
        public void GoTo(string url){
#if UNITY_EDITOR || !UNITY_WEBGL
            Application.OpenURL(url);
#else
            ShowConfirmUrl(url);
#endif
        }
    }
}

