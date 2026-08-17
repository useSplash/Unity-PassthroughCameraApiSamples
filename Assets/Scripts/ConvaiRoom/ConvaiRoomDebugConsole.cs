using System.Collections.Generic;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace ConvaiRoom
{
    /// <summary>
    /// Keyboard driver for exercising the room actions without speaking into a mic.
    ///
    /// Two layers, deliberately independent, because a silent character has two very
    /// different causes and guessing between them wastes a four-minute Play cycle:
    ///
    /// - <b>Text prompts</b> go through the entire real pipeline. The backend receives the
    ///   text, decides whether to act, picks the target by name, and sends the command
    ///   back -- identical to speech in every respect except the input device. This is
    ///   what proves the catalogue arrived and the model can act on it.
    /// - <b>Direct pathing</b> skips Convai completely and drives the agent straight at a
    ///   catalogue object. When a spoken command does nothing, this separates "the model
    ///   never issued a command" from "the navmesh cannot reach that object".
    ///
    /// If a text prompt produces no movement but direct pathing to the same object works,
    /// the navmesh is fine and the problem is upstream in the action config.
    /// </summary>
    public class ConvaiRoomDebugConsole : MonoBehaviour
    {
        [System.Serializable]
        public class DebugPrompt
        {
            public KeyCode key;

            [TextArea(1, 3)]
            public string text;
        }

        [Header("Wiring")]
        [Tooltip("Leave empty to find the one in the scene.")]
        public ConvaiPlayer player;

        [Tooltip("Leave empty to find the one in the scene.")]
        public RoomScanActionConfigBuilder actionConfigBuilder;

        [Tooltip("Leave empty to find the one in the scene.")]
        public NavMeshAgent agent;

        [Header("Text prompts (full pipeline)")]
        [Tooltip("Sent as if the player had spoken them. The backend still chooses the " +
                 "action and the target, so these test the catalogue as well as the actions.")]
        public DebugPrompt[] prompts =
        {
            new DebugPrompt { key = KeyCode.Alpha1, text = "What do you see in this room?" },
            new DebugPrompt { key = KeyCode.Alpha2, text = "Walk to the chair." },
            new DebugPrompt { key = KeyCode.Alpha3, text = "Look at the laptop." },
            new DebugPrompt { key = KeyCode.Alpha4, text = "What is next to the keyboard?" }
        };

        [Header("Direct pathing (bypasses Convai)")]
        [Tooltip("Cycles which catalogue object the direct-path key aims at.")]
        public KeyCode cycleTargetKey = KeyCode.Tab;

        [Tooltip("Paths the agent at the selected object without involving the backend.")]
        public KeyCode pathToTargetKey = KeyCode.Backspace;

        [Tooltip("Logs agent and navmesh state.")]
        public KeyCode reportStateKey = KeyCode.BackQuote;

        [Header("Safety")]
        [Tooltip("Switches itself off outside the Editor and development builds. This reads " +
                 "raw keyboard input and has no business running on a headset.")]
        public bool editorAndDevBuildsOnly = true;

        private int _targetIndex;

        private void Awake()
        {
            if (editorAndDevBuildsOnly && !Application.isEditor && !Debug.isDebugBuild)
            {
                enabled = false;
                return;
            }

            if (player == null) player = FindAnyObjectByType<ConvaiPlayer>();
            if (actionConfigBuilder == null)
                actionConfigBuilder = FindAnyObjectByType<RoomScanActionConfigBuilder>();
            if (agent == null) agent = FindAnyObjectByType<NavMeshAgent>();

            if (player == null)
            {
                Debug.LogError("[ConvaiRoomDebugConsole] No ConvaiPlayer in the scene, so text " +
                               "prompts cannot be sent. Direct pathing still works.");
            }
        }

        private void Update()
        {
            foreach (var prompt in prompts)
            {
                if (prompt == null || prompt.key == KeyCode.None) continue;
                if (!Input.GetKeyDown(prompt.key)) continue;

                Send(prompt.text);
            }

            if (Input.GetKeyDown(cycleTargetKey)) CycleTarget();
            if (Input.GetKeyDown(pathToTargetKey)) PathToSelected();
            if (Input.GetKeyDown(reportStateKey)) ReportState();
        }

        /// <summary>Sends text as though the player had spoken it.</summary>
        public void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (player == null)
            {
                Debug.LogError("[ConvaiRoomDebugConsole] No ConvaiPlayer to send through.");
                return;
            }

            Debug.Log($"[ConvaiRoomDebugConsole] Sending: \"{text}\"");
            player.SendTextMessage(text);
        }

        private IReadOnlyList<ConvaiActionObjectDefinition> Catalogue =>
            actionConfigBuilder != null ? actionConfigBuilder.LastObjects : null;

        private void CycleTarget()
        {
            var catalogue = Catalogue;
            if (catalogue == null || catalogue.Count == 0)
            {
                Debug.LogWarning("[ConvaiRoomDebugConsole] Catalogue is empty -- either the " +
                                 "scan has not been replayed yet or every object was filtered out.");
                return;
            }

            _targetIndex = (_targetIndex + 1) % catalogue.Count;
            var selected = catalogue[_targetIndex];

            Debug.Log($"[ConvaiRoomDebugConsole] Target {_targetIndex + 1}/{catalogue.Count}: " +
                      $"{selected.Name}");
        }

        /// <summary>
        /// Paths at the selected object without going through Convai. Reports the path
        /// status before moving, because a PathPartial result is the interesting case --
        /// the agent will set off, look like it is working, and stop short of the target.
        /// </summary>
        private void PathToSelected()
        {
            var catalogue = Catalogue;
            if (catalogue == null || catalogue.Count == 0)
            {
                Debug.LogWarning("[ConvaiRoomDebugConsole] Nothing in the catalogue to path to.");
                return;
            }

            if (_targetIndex >= catalogue.Count) _targetIndex = 0;
            var selected = catalogue[_targetIndex];

            if (selected?.GameObjectReference == null)
            {
                Debug.LogError($"[ConvaiRoomDebugConsole] '{selected?.Name}' has no proxy object, " +
                               $"so Move To could not resolve it either.");
                return;
            }

            if (agent == null || !agent.enabled)
            {
                Debug.LogError("[ConvaiRoomDebugConsole] Agent is missing or disabled -- the bake " +
                               "probably failed, so nothing can path anywhere.");
                return;
            }

            if (!agent.isOnNavMesh)
            {
                Debug.LogError("[ConvaiRoomDebugConsole] Agent is not on the navmesh, so it cannot " +
                               "path. Placement failed even though the bake reported success.");
                return;
            }

            var destination = selected.GameObjectReference.transform.position;
            var path = new NavMeshPath();
            var found = agent.CalculatePath(destination, path);

            Debug.Log($"[ConvaiRoomDebugConsole] Path to '{selected.Name}': " +
                      $"{(found ? path.status.ToString() : "NO PATH")} " +
                      $"({path.corners.Length} corners) -> {destination}");

            if (!found) return;

            agent.isStopped = false;
            agent.SetDestination(destination);
        }

        private void ReportState()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var catalogue = Catalogue;

            Debug.Log($"[ConvaiRoomDebugConsole] navmesh {triangulation.vertices.Length} verts / " +
                      $"{triangulation.indices.Length / 3} tris | " +
                      $"catalogue {(catalogue?.Count ?? 0)} objects | " +
                      $"agent enabled={(agent != null && agent.enabled)} " +
                      $"onNavMesh={(agent != null && agent.isOnNavMesh)} " +
                      $"pos={(agent != null ? agent.transform.position.ToString() : "n/a")}");
        }
    }
}
