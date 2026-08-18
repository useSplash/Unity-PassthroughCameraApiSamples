using Convai.Runtime.Components;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Keyboard driver for exercising the character without speaking into a mic.
    ///
    /// Prompts go through the entire real pipeline: the backend receives the text, decides
    /// what to say, and responds -- identical to speech in every respect except the input
    /// device. That makes a silent character here a backend or session problem, not a
    /// microphone one, which is the split that otherwise costs a four-minute Play cycle to
    /// work out.
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

        [Header("Text prompts (full pipeline)")]
        [Tooltip("Sent as if the player had spoken them.")]
        public DebugPrompt[] prompts =
        {
            new DebugPrompt { key = KeyCode.Alpha1, text = "Hello, can you hear me?" },
            new DebugPrompt { key = KeyCode.Alpha2, text = "What do you see in this room?" },
            new DebugPrompt { key = KeyCode.Alpha3, text = "Tell me about yourself." }
        };

        [Header("Safety")]
        [Tooltip("Switches itself off outside the Editor and development builds. This reads " +
                 "raw keyboard input and has no business running on a headset.")]
        public bool editorAndDevBuildsOnly = true;

        private void Awake()
        {
            if (editorAndDevBuildsOnly && !Application.isEditor && !Debug.isDebugBuild)
            {
                enabled = false;
                return;
            }

            if (player == null) player = FindAnyObjectByType<ConvaiPlayer>();

            if (player == null)
            {
                Debug.LogError("[ConvaiRoomDebugConsole] No ConvaiPlayer in the scene, so text " +
                               "prompts cannot be sent.");
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
    }
}
