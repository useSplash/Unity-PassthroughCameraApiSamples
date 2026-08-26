using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ConvaiRoom
{
    /// <summary>
    /// Asks Claude for a plan and gets back structured steps rather than prose.
    ///
    /// Raw HTTP through <see cref="UnityWebRequest"/> rather than the official Anthropic C# SDK,
    /// and that is a runtime decision rather than a preference. The SDK is a NuGet package built
    /// for modern .NET; this is Unity 6 on .NET Standard 2.1, built to IL2CPP for Android ARM64.
    /// Dropping the package and its transitive dependencies into Assets/Plugins means resolving
    /// them against Unity's own assemblies by hand and then hoping a reflection-driven JSON
    /// serialiser survives AOT compilation and managed stripping. One HTTPS POST does not justify
    /// that, and UnityWebRequest is the path Unity actually supports on device.
    ///
    /// The response is constrained with STRUCTURED OUTPUTS -- output_config.format with a JSON
    /// schema -- rather than asked for politely in the prompt. That matters more here than in an
    /// ordinary integration: the schema's `where` field is an enum built from the room's live
    /// action targets, so the model cannot name a place this room does not have. The grounding
    /// is enforced by the API rather than validated afterwards, which is the difference between
    /// a plan that always resolves and a plan that usually does.
    ///
    /// Nothing here is on a per-frame path. One request per planning turn, and the action's own
    /// timeout is what bounds it.
    /// </summary>
    public class RoomPlannerClient : MonoBehaviour
    {
        private const string Tag = "[RoomPlanner]";

        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        /// <summary>
        /// Where a side-loaded key is looked for, under <see cref="Application.persistentDataPath"/>.
        /// Checked before the bundled one so a key can be swapped on device without a rebuild.
        /// </summary>
        private const string KeyFileName = "planner_key.txt";

        /// <summary>Resources path of the bundled key. Kept out of git -- see the class remarks.</summary>
        private const string KeyResourceName = "planner_key";

        [Header("Model")]
        [Tooltip("Which Claude model plans the task. Left at the default unless you have a " +
                 "reason -- this is the one the planner prompt was written against.")]
        public string model = "claude-opus-5";

        [Tooltip("How hard the model works before answering.\n\n" +
                 "Low on purpose. Enumerating five grounded steps from a fixed list of places " +
                 "is not a hard problem, and every extra second here is a second the character " +
                 "stands silent in front of you. Raise it if plans come back thin.")]
        public string effort = "low";

        [Tooltip("Ceiling on the reply, thinking included. A plan is short; this is sized so a " +
                 "long think cannot truncate the JSON halfway through a step.")]
        public int maxTokens = 8000;

        [Tooltip("Seconds before the request is abandoned. Keep this under the Plan Task " +
                 "action's own timeout, or the action gives up first and the answer is wasted.")]
        public int timeoutSeconds = 25;

        [Header("Debug")]
        [Tooltip("Log the request and the raw reply. Noisy, and the reply contains the whole " +
                 "plan -- for bring-up only.")]
        public bool verboseLogging;

        /// <summary>One step as the planner returned it, before it is given a number.</summary>
        public readonly struct PlannedStep
        {
            public readonly string Text;

            /// <summary>A place from the offered vocabulary, or null for a step with no home.</summary>
            public readonly string Where;

            public PlannedStep(string text, string where)
            {
                Text = text;
                Where = where;
            }
        }

        /// <summary>What came back, or why nothing did.</summary>
        public readonly struct PlanResult
        {
            public readonly bool Ok;

            /// <summary>One sentence naming the task, for the panel and the spoken lead-in.</summary>
            public readonly string Summary;

            public readonly IReadOnlyList<PlannedStep> Steps;

            /// <summary>Short enough for the panel. Empty when <see cref="Ok"/>.</summary>
            public readonly string Failure;

            private PlanResult(bool ok, string summary, IReadOnlyList<PlannedStep> steps, string failure)
            {
                Ok = ok;
                Summary = summary;
                Steps = steps;
                Failure = failure;
            }

            public static PlanResult Success(string summary, IReadOnlyList<PlannedStep> steps) =>
                new PlanResult(true, summary, steps, "");

            public static PlanResult Failed(string failure) =>
                new PlanResult(false, "", Array.Empty<PlannedStep>(), failure);
        }

        private string _cachedKey;
        private bool _keyLookedFor;

        /// <summary>
        /// Whether a key was found. Read by the panel and the health probe, because a missing
        /// key is the single most likely reason planning does nothing, and it is invisible from
        /// inside the headset.
        /// </summary>
        public bool HasKey => !string.IsNullOrEmpty(ResolveKey());

        /// <summary>Plans <paramref name="task"/> against the places in <paramref name="places"/>.</summary>
        public async Task<PlanResult> PlanAsync(
            string task,
            IReadOnlyList<RoomTaskVocabulary.Place> places,
            string roomSummary,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(task))
                return PlanResult.Failed("no task was given");

            var key = ResolveKey();
            if (string.IsNullOrEmpty(key))
            {
                return PlanResult.Failed("no planner API key on this device");
            }

            var body = BuildRequest(task, places, roomSummary);

            if (verboseLogging) Debug.Log($"{Tag} Planning '{task}' against {places.Count} places.");

            string raw;
            try
            {
                raw = await PostAsync(body, key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} The planning request failed: {ex.Message}");
                return PlanResult.Failed("could not reach the planner");
            }

            if (verboseLogging) Debug.Log($"{Tag} Raw reply: {raw}");

            return Parse(raw, places);
        }

        // -----------------------------------------------------------------
        // Request
        // -----------------------------------------------------------------

        /// <summary>
        /// Writes the request body by hand.
        ///
        /// JsonUtility cannot express this: the schema is a nested object graph with a
        /// dynamically built enum, and JsonUtility has no dictionary support and no way to emit
        /// an array of arbitrary strings inside a nested type without a class per level. A
        /// StringBuilder is shorter than the class hierarchy it would take to avoid one.
        /// </summary>
        private string BuildRequest(string task, IReadOnlyList<RoomTaskVocabulary.Place> places,
                                    string roomSummary)
        {
            var json = new StringBuilder(2048);

            json.Append('{');
            json.Append("\"model\":").Append(Quote(model)).Append(',');
            json.Append("\"max_tokens\":").Append(Mathf.Max(1024, maxTokens)).Append(',');

            json.Append("\"output_config\":{");
            json.Append("\"effort\":").Append(Quote(effort)).Append(',');
            json.Append("\"format\":{\"type\":\"json_schema\",\"schema\":");
            AppendSchema(json, places);
            json.Append("}},");

            json.Append("\"system\":").Append(Quote(BuildSystemPrompt(places, roomSummary))).Append(',');

            json.Append("\"messages\":[{\"role\":\"user\",\"content\":")
                .Append(Quote(task.Trim()))
                .Append("}]");

            json.Append('}');

            return json.ToString();
        }

        /// <summary>
        /// The schema, and with it the grounding guarantee.
        ///
        /// `where` is an enum of the room's own place names unioned with null. The union is what
        /// keeps the plan honest: a step that genuinely has nowhere to happen in this room says
        /// so, instead of being forced to pick the least wrong piece of furniture. Given the
        /// scan's vocabulary is a fixed detector class list, that case is common rather than
        /// exceptional.
        ///
        /// Every object carries additionalProperties:false because the API requires it, and no
        /// string or array length constraints appear because structured outputs reject them --
        /// step count is asked for in the prompt and clamped after parsing instead.
        /// </summary>
        private static void AppendSchema(StringBuilder json, IReadOnlyList<RoomTaskVocabulary.Place> places)
        {
            json.Append("{\"type\":\"object\",\"additionalProperties\":false,");
            json.Append("\"required\":[\"summary\",\"steps\"],\"properties\":{");

            json.Append("\"summary\":{\"type\":\"string\",\"description\":")
                .Append(Quote("One short sentence naming what this plan achieves."))
                .Append("},");

            json.Append("\"steps\":{\"type\":\"array\",\"description\":")
                .Append(Quote("The steps in order, first to last."))
                .Append(",\"items\":{\"type\":\"object\",\"additionalProperties\":false,");
            json.Append("\"required\":[\"text\",\"where\"],\"properties\":{");

            json.Append("\"text\":{\"type\":\"string\",\"description\":")
                .Append(Quote("What to do, as one instruction said out loud. No step number."))
                .Append("},");

            json.Append("\"where\":{\"description\":")
                .Append(Quote("The place in this room where this step happens, chosen from the " +
                              "listed places, or null when no listed place fits."))
                .Append(",\"anyOf\":[{\"type\":\"string\",\"enum\":[");

            for (var i = 0; i < places.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(Quote(places[i].Name));
            }

            json.Append("]},{\"type\":\"null\"}]}");

            json.Append("}}}");  // steps.items.properties, steps.items, steps
            json.Append("}}");   // properties, root
        }

        /// <summary>
        /// What the planner is told about the room before it is asked anything.
        ///
        /// The place list is repeated here in prose even though it is already an enum in the
        /// schema, because the enum constrains the ANSWER while this shapes the THINKING: the
        /// descriptions are what let it tell a dining table from a desk, and pick the step order
        /// that involves the least walking. The enum alone would give it names with no idea what
        /// they are.
        /// </summary>
        private static string BuildSystemPrompt(IReadOnlyList<RoomTaskVocabulary.Place> places,
                                                string roomSummary)
        {
            var prompt = new StringBuilder(1024);

            prompt.Append(
                "You plan practical tasks for a person wearing a mixed-reality headset, who is " +
                "standing in the room described below and can see it. A virtual character reads " +
                "your steps out loud and can walk to the places you name.\n\n");

            prompt.Append(
                "Write the fewest steps that genuinely complete the task, normally three to six " +
                "and never more than eight. Each step is one plain instruction said out loud to " +
                "someone standing there -- no numbering, no preamble, no restating the task.\n\n");

            prompt.Append(
                "Ground each step in the room where you honestly can. Set `where` to the place " +
                "the step happens at. Set it to null when no listed place is where that step " +
                "happens -- a step that is thinking, waiting, or done somewhere this room does " +
                "not contain. Never pick a nearly-right place to avoid a null; a confidently " +
                "wrong location sends the character to the wrong side of the room.\n\n");

            if (!string.IsNullOrWhiteSpace(roomSummary))
                prompt.Append("The room: ").Append(roomSummary.Trim()).Append("\n\n");

            if (places.Count == 0)
            {
                prompt.Append(
                    "This room has no recognised places yet, so `where` must be null on every " +
                    "step. Say the steps anyway.");

                return prompt.ToString();
            }

            prompt.Append("Places in this room, and the only values `where` may take:\n");

            foreach (var place in places)
            {
                prompt.Append("- ").Append(place.Name);

                if (!string.IsNullOrWhiteSpace(place.Description))
                    prompt.Append(": ").Append(place.Description.Trim());

                prompt.Append('\n');
            }

            return prompt.ToString();
        }

        // -----------------------------------------------------------------
        // Transport
        // -----------------------------------------------------------------

        /// <summary>
        /// Posts the request and returns the body, whatever the status code.
        ///
        /// A non-2xx is not thrown on here: the API puts a readable reason in the body of a 400
        /// and throwing on the status would discard exactly the sentence worth reporting. The
        /// parse step reads the error shape and surfaces it.
        /// </summary>
        private async Task<string> PostAsync(string body, string key, CancellationToken cancellationToken)
        {
            using var request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST);

            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(5, timeoutSeconds);

            request.SetRequestHeader("content-type", "application/json");
            request.SetRequestHeader("x-api-key", key);
            request.SetRequestHeader("anthropic-version", ApiVersion);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var operation = request.SendWebRequest();
            operation.completed += _ => completion.TrySetResult(true);

            // Abort rather than just stop waiting. A cancelled planning turn whose request keeps
            // running would land its reply into a plan nobody asked for any more, and on a
            // headset it is also a radio left on.
            using (cancellationToken.Register(() =>
            {
                if (!request.isDone) request.Abort();
                completion.TrySetCanceled();
            }))
            {
                await completion.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new IOException(request.error);
            }

            return request.downloadHandler.text;
        }

        // -----------------------------------------------------------------
        // Response
        // -----------------------------------------------------------------

#pragma warning disable 0649  // Assigned by JsonUtility, which the compiler cannot see.

        [Serializable]
        private class MessageEnvelope
        {
            public ContentBlock[] content;
            public string stop_reason;
            public ApiError error;
        }

        [Serializable]
        private class ContentBlock
        {
            public string type;
            public string text;
        }

        [Serializable]
        private class ApiError
        {
            public string type;
            public string message;
        }

        [Serializable]
        private class PlanPayload
        {
            public string summary;
            public PayloadStep[] steps;
        }

        [Serializable]
        private class PayloadStep
        {
            public string text;
            public string where;
        }

#pragma warning restore 0649

        private PlanResult Parse(string raw, IReadOnlyList<RoomTaskVocabulary.Place> places)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return PlanResult.Failed("the planner returned nothing");

            MessageEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<MessageEnvelope>(raw);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read the reply envelope: {ex.Message}");
                return PlanResult.Failed("the planner's reply was unreadable");
            }

            if (envelope == null) return PlanResult.Failed("the planner's reply was empty");

            if (envelope.error != null && !string.IsNullOrEmpty(envelope.error.message))
            {
                Debug.LogWarning($"{Tag} The API refused the request: {envelope.error.message}");
                return PlanResult.Failed(Shorten(envelope.error.message));
            }

            // Structured outputs do not survive a refusal -- the docs are explicit that the
            // reply may not match the schema in that case -- so it is checked before the text is
            // trusted rather than after the parse fails confusingly.
            if (envelope.stop_reason == "refusal")
                return PlanResult.Failed("the planner declined this task");

            if (envelope.stop_reason == "max_tokens")
                return PlanResult.Failed("the plan was cut off; raise Max Tokens");

            var text = FirstText(envelope);
            if (string.IsNullOrWhiteSpace(text))
                return PlanResult.Failed("the planner returned no plan");

            PlanPayload payload;
            try
            {
                payload = JsonUtility.FromJson<PlanPayload>(text);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read the plan itself: {ex.Message}");
                return PlanResult.Failed("the plan came back malformed");
            }

            if (payload?.steps == null || payload.steps.Length == 0)
                return PlanResult.Failed("the planner produced no steps");

            var steps = new List<PlannedStep>(payload.steps.Length);

            foreach (var step in payload.steps)
            {
                if (step == null || string.IsNullOrWhiteSpace(step.text)) continue;

                // A place the room no longer has is dropped to null rather than kept. The schema
                // enum makes this rare, but a scan reloaded while the request was in flight is a
                // real case, and an unlocated step is honest where a dangling one is not.
                var where = RoomTaskVocabulary.Contains(places, step.where) ? step.where : null;

                if (!string.IsNullOrEmpty(step.where) && where == null)
                {
                    Debug.LogWarning($"{Tag} Dropped the location '{step.where}' from a step -- " +
                                     $"the room does not have it any more.");
                }

                steps.Add(new PlannedStep(step.text.Trim(), where));
            }

            if (steps.Count == 0) return PlanResult.Failed("the planner produced no usable steps");

            return PlanResult.Success(
                string.IsNullOrWhiteSpace(payload.summary) ? "" : payload.summary.Trim(), steps);
        }

        /// <summary>
        /// The first text block in the reply.
        ///
        /// Indexing content[0] would work today and break the moment thinking is turned up:
        /// thinking blocks arrive in the same array and come first.
        /// </summary>
        private static string FirstText(MessageEnvelope envelope)
        {
            if (envelope.content == null) return null;

            foreach (var block in envelope.content)
                if (block != null && block.type == "text" && !string.IsNullOrWhiteSpace(block.text))
                    return block.text;

            return null;
        }

        // -----------------------------------------------------------------
        // Key
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds the API key, preferring one side-loaded onto the device.
        ///
        /// Two sources, in this order, and the order is the useful part: a file under
        /// persistentDataPath can be replaced by pushing one file, with no rebuild and nothing
        /// in the APK, which is how a key gets rotated on a headset that has no keyboard. The
        /// bundled Resources copy is the fallback that makes the build self-contained.
        ///
        /// Neither belongs in git. `origin` here is a public fork, and an API key committed to
        /// it is a key that has to be revoked -- Assets/Resources/planner_key.txt is in
        /// .gitignore for exactly the reason ConvaiSettings.asset is kept out.
        /// </summary>
        private string ResolveKey()
        {
            if (_keyLookedFor) return _cachedKey;

            _keyLookedFor = true;

            var path = Path.Combine(Application.persistentDataPath, KeyFileName);

            try
            {
                if (File.Exists(path))
                {
                    _cachedKey = File.ReadAllText(path).Trim();

                    if (!string.IsNullOrEmpty(_cachedKey))
                    {
                        Debug.Log($"{Tag} Using the planner key from {KeyFileName}.");
                        return _cachedKey;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read {path}: {ex.Message}");
            }

            var bundled = Resources.Load<TextAsset>(KeyResourceName);

            if (bundled != null && !string.IsNullOrWhiteSpace(bundled.text))
            {
                _cachedKey = bundled.text.Trim();
                return _cachedKey;
            }

            Debug.LogWarning(
                $"{Tag} No planner key. Put one in Assets/Resources/{KeyResourceName}.txt " +
                $"(gitignored) before building, or push one to " +
                $"{Application.persistentDataPath}/{KeyFileName} on the headset. Until then " +
                $"she can still talk about the room but cannot plan anything.");

            _cachedKey = null;
            return null;
        }

        /// <summary>Forgets the cached key, so a newly pushed file is picked up without a restart.</summary>
        public void ForgetKey()
        {
            _keyLookedFor = false;
            _cachedKey = null;
        }

        // -----------------------------------------------------------------
        // Words
        // -----------------------------------------------------------------

        private static string Shorten(string text) =>
            string.IsNullOrEmpty(text) || text.Length <= 80 ? text : text.Substring(0, 77) + "...";

        /// <summary>A JSON string literal, escaped to the spec.</summary>
        private static string Quote(string value)
        {
            if (value == null) return "null";

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        // Control characters are the only other thing JSON forbids raw. Anything
                        // printable, non-ASCII included, goes through as UTF-8.
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
