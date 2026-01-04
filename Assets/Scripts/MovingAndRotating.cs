using UnityEngine; 
 
public class MovingAndRotating : MonoBehaviour 
{ 
    public float moveSpeed = 1f; 
    public float rotateSpeed = 30f; 
    public float moveDistance = 0.5f; 
 
    private Vector3 startPos; 
    private bool movingUp = true; 
 
    private void Start() 
    { 
        startPos = transform.position; 
    } 
 
    private void Update() 
    { 
        // Вращение 
        transform.Rotate(Vector3.up * rotateSpeed * Time.deltaTime); 
 
        // Движение вверх–вниз 
        float offset = moveSpeed * Time.deltaTime; 
        if (movingUp) 
            transform.position += Vector3.up * offset; 
        else 
            transform.position -= Vector3.up * offset; 
 
        if (Vector3.Distance(transform.position, startPos) >= moveDistance) 
            movingUp = !movingUp; 
    } 
}