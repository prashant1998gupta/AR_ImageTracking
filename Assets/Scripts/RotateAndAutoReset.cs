using UnityEngine;
using System.Collections;

public class RotateAndAutoReset : MonoBehaviour
{
    public float rotationSpeed = 10f; // Speed for mouse input
    public float touchRotationSpeed = 0.1f; // Speed for touch input
    public float resetDuration = 1f; // Duration of the reset animation

    private Vector3 initialPosition;
    private Quaternion initialRotation;

    private bool isInteracting = false;
    private Coroutine resetCoroutine;

    void Start()
    {
        // Store the initial position and rotation
        initialPosition = transform.position;
        initialRotation = transform.rotation;
    }

    void Update()
    {
#if UNITY_EDITOR || UNITY_WEBGL
        HandleMouseInput();
#else
        HandleTouchInput();
#endif
    }

    void HandleMouseInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isInteracting = true;
            if (resetCoroutine != null)
            {
                StopCoroutine(resetCoroutine);
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            isInteracting = false;
            StartResetCoroutine();
        }

        if (isInteracting)
        {
            RotateWithMouse();
        }
    }

    void HandleTouchInput()
    {
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);

            if (touch.phase == TouchPhase.Began)
            {
                isInteracting = true;
                if (resetCoroutine != null)
                {
                    StopCoroutine(resetCoroutine);
                }
            }
            else if (touch.phase == TouchPhase.Moved && isInteracting)
            {
                RotateWithTouch(touch);
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                isInteracting = false;
                StartResetCoroutine();
            }
        }
    }

    void RotateWithMouse()
    {
        float mouseX = Input.GetAxis("Mouse X") * rotationSpeed;
        float mouseY = Input.GetAxis("Mouse Y") * rotationSpeed;

        // Rotate the object around the Y-axis and X-axis
        transform.Rotate(Vector3.up, -mouseX, Space.World);
        transform.Rotate(Vector3.right, mouseY, Space.World);
    }

    void RotateWithTouch(Touch touch)
    {
        float deltaX = touch.deltaPosition.x * touchRotationSpeed;
        float deltaY = touch.deltaPosition.y * touchRotationSpeed;

        // Rotate the object around the Y-axis and X-axis
        transform.Rotate(Vector3.up, -deltaX, Space.World);
        transform.Rotate(Vector3.right, deltaY, Space.World);
    }

    void StartResetCoroutine()
    {
        if (resetCoroutine != null)
        {
            StopCoroutine(resetCoroutine);
        }
        resetCoroutine = StartCoroutine(ResetTransform());
    }

    IEnumerator ResetTransform()
    {
        float elapsedTime = 0f;
        Vector3 startingPosition = transform.position;
        Quaternion startingRotation = transform.rotation;

        while (elapsedTime < resetDuration)
        {
            transform.position = Vector3.Lerp(startingPosition, initialPosition, elapsedTime / resetDuration);
            transform.rotation = Quaternion.Lerp(startingRotation, initialRotation, elapsedTime / resetDuration);
            elapsedTime += Time.deltaTime;
            yield return null;
        }

        transform.position = initialPosition;
        transform.rotation = initialRotation;
    }
}
