using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class UIManager : MonoBehaviour
{

    public Button contectButton;

    // Start is called before the first frame update
    void Start()
    {
        contectButton.onClick.AddListener(OpenURL);
    }

   void OpenURL()
    {
        Application.OpenURL("https://rionick.com/contact-us");
    }
}
