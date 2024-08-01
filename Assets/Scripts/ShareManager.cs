using UnityEngine;

public class ShareManager : MonoBehaviour
{
    public void ShareText1(string text, string url)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        Application.ExternalCall("shareText", text, url);
#endif
    }

    public void ShareText(string text, string url)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        string jsCode = $"shareText('{text}', '{url}');";
        Application.ExternalEval(jsCode);
#endif
    }
}
