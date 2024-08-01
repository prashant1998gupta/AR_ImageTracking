using UnityEngine;
using UnityEngine.UI;

public class ExampleUsage : MonoBehaviour
{
    public ShareManager shareManager;
    public Button shareButton;

    private void Start()
    {
        shareButton.onClick.RemoveAllListeners();
        shareButton.onClick.AddListener(Share); 
    }

    public void Share()
    {
        shareManager.ShareText("Check out this cool link!", "https://example.com");
    }
}
