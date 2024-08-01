using UnityEngine;
using UnityEngine.UI;

public class SliderAnimationController : MonoBehaviour
{
    public Animator sliderAnimator;
    public Button playButton;
    private bool isOn = false;

    void Start()
    {
        playButton.onClick.AddListener(ToggleAnimation);
    }

    void ToggleAnimation()
    {
        isOn = !isOn;
        if(isOn)
        {
            sliderAnimator.SetTrigger("IsOn");
        }
        else
        {
            sliderAnimator.SetTrigger("IsOff");
        }
        
    }
}
