using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Imagine.WebAR.Samples
{
    public class Billboard : MonoBehaviour
    {
        [SerializeField] Camera mainCamera;
        
        private enum BillboardMode {Y_UP, ALL_AXES}
        [SerializeField] BillboardMode mode = BillboardMode.Y_UP;
        [SerializeField] bool negative = false;

        void LateUpdate()
        {
            if (mode == BillboardMode.ALL_AXES){
                transform.LookAt(mainCamera.transform);
            }
            else{

                transform.LookAt(mainCamera.transform);
                var eul = transform.localEulerAngles;
                eul.x = 0;
                eul.z = 0;
                transform.localEulerAngles = eul;
            }

            transform.Rotate(0, 180, 0);
        }
    }
}
