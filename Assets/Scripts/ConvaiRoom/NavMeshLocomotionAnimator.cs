using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// Feeds NavMeshAgent speed into an Animator float so a locomotion blend tree can
    /// react to pathfinding.
    ///
    /// The Convai SDK ships an idle loop and a talk loop but no walk cycle, so with the
    /// stock clips this drives a parameter nothing blends on yet and the character glides
    /// along its path. Drop a walk clip into the blend tree's upper slot and it starts
    /// working with no code change.
    ///
    /// The Animator here must have Apply Root Motion OFF -- the agent owns position, and
    /// root motion fighting it produces a character that drifts off its own path.
    /// </summary>
    public class NavMeshLocomotionAnimator : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("Leave empty to search this GameObject.")]
        public NavMeshAgent agent;

        [Tooltip("Leave empty to search children -- the Animator lives on the mesh, which " +
                 "is usually a child of the Convai character root.")]
        public Animator animator;

        [Header("Parameter")]
        public string speedParameter = "Speed";

        [Tooltip("Normalises speed against the agent's own max, so the blend tree can run " +
                 "0..1 and stay correct if the agent speed is retuned.")]
        public bool normalize = true;

        [Tooltip("Smoothing time. Without this the value snaps on every path recalculation " +
                 "and the blend visibly pops.")]
        public float damping = 0.15f;

        private int _speedHash;
        private float _current;
        private float _velocity;
        private bool _hasParameter;

        private void Awake()
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (animator == null) animator = GetComponentInChildren<Animator>();

            _speedHash = Animator.StringToHash(speedParameter);
            _hasParameter = HasFloatParameter();

            if (animator != null && animator.applyRootMotion)
            {
                Debug.LogWarning($"[NavMeshLocomotionAnimator] {animator.name} has Apply Root " +
                                 $"Motion enabled. The NavMeshAgent already drives position, so " +
                                 $"the two will fight. Turning it off.");
                animator.applyRootMotion = false;
            }
        }

        private void Update()
        {
            if (!_hasParameter || agent == null || !agent.enabled) return;

            var speed = agent.velocity.magnitude;

            if (normalize && agent.speed > 0.01f)
                speed = Mathf.Clamp01(speed / agent.speed);

            _current = Mathf.SmoothDamp(_current, speed, ref _velocity, damping);
            animator.SetFloat(_speedHash, _current);
        }

        /// <summary>
        /// Setting a parameter an Animator does not have logs an error every frame, so
        /// check once up front and stay quiet if the controller has not been wired yet.
        /// </summary>
        private bool HasFloatParameter()
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;

            foreach (var parameter in animator.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float &&
                    parameter.nameHash == _speedHash)
                    return true;

            Debug.LogWarning($"[NavMeshLocomotionAnimator] No float parameter " +
                             $"'{speedParameter}' on the controller; locomotion blending is off.");
            return false;
        }
    }
}
