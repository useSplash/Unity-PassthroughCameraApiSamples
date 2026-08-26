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
    /// Asks a model for a plan and gets back structured steps rather than prose.
    ///
    /// Two backends, chosen in the inspector, because they answer different questions. Ollama
    /// on a machine on your network is free and private and needs that machine switched on;
    /// Anthropic costs a fraction of a penny a plan and works from anywhere. Keeping both is
    /// what lets you find out whether a 7B model is actually good enough at this, which is not
    /// a question anybody can answer for you from first principles.
    ///
    /// Raw HTTP through <see cref="UnityWebRequest"/> rather than a vendor SDK, and that is a
    /// runtime decision rather than a preference. The Anthropic C# SDK is a NuGet package built
    /// for modern .NET; this is Unity 6 on .NET Standard 2.1, built to IL2CPP for Android ARM64.
    /// Dropping that package and its transitive dependencies into Assets/Plugins means resolving
    /// them against Unity's own assemblies by hand and then hoping a reflection-driven JSON
    /// serialiser survives AOT compilation and managed stripping. One HTTPS POST does not justify
    /// that, and UnityWebRequest is the path Unity actually supports on device.
    ///
    /// The response is constrained with STRUCTURED OUTPUTS -- a JSON schema the runtime enforces
    /// during generation -- rather than asked for politely in the prompt. That matters more here
    /// than in an ordinary integration: the schema's `where` field is an enum built from the
    /// room's live action targets, so the model CANNOT name a place this room does not have. It
    /// is the difference between a plan that always resolves and a plan that usually does, and
    /// it is the single reason this feature works with a small local model at all.
    ///
    /// That both backends enforce the same schema is what makes them interchangeable. Anthropic
    /// nests it under output_config.format; Ollama takes it bare in `format` and compiles it to
    /// a grammar. Everything either side of that -- the prompt, the schema itself, the parsing,
    /// the plan -- is shared.
    ///
    /// Nothing here is on a per-frame path. One request per planning turn, and the action's own
    /// timeout is what bounds it.
    /// </summary>
    public class RoomPlannerClient : MonoBehaviour
    {
        private const string Tag = "[RoomPlanner]";

        private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
        private const string AnthropicApiVersion = "2023-06-01";

        /// <summary>
        /// What the schema offers for a step that happens nowhere in particular.
        ///
        /// A sentinel string inside the enum rather than a JSON null, and the reason is
        /// portability. Expressing "one of these names, or nothing" properly needs
        /// anyOf[enum, null], which Anthropic handles cleanly and grammar-based constrainers
        /// handle unevenly -- llama.cpp compiles the schema to a grammar, and null unions are
        /// exactly the corner where that gets ragged. One more enum member is understood
        /// identically by everything, and it costs one comparison when the reply is read.
        /// </summary>
        private const string NoPlace = "none";

        /// <summary>
        /// Where a side-loaded key is looked for, under <see cref="Application.persistentDataPath"/>.
        /// Checked before the bundled one so a key can be swapped on device without a rebuild.
        /// </summary>
        private const string KeyFileName = "planner_key.txt";

        /// <summary>Resources path of the bundled key. Kept out of git -- see the class remarks.</summary>
        private const string KeyResourceName = "planner_key";

        /// <summary>Which service works the plan out.</summary>
        public enum PlannerBackend
        {
            /// <summary>A model on your own network, through Ollama. Free, private, tethered.</summary>
            Ollama = 0,

            /// <summary>Anthropic's API. Costs a fraction of a penny a plan, works anywhere.</summary>
            Anthropic = 1
        }

        [Header("Backend")]
        [Tooltip("Who works the plan out.\n\n" +
                 "Ollama runs a model on a machine on your network: free, nothing leaves the " +
                 "house, and it needs that machine switched on and reachable from the headset. " +
                 "Anthropic works from anywhere and costs a fraction of a penny a plan.\n\n" +
                 "Both are held to the same JSON schema, so switching between them changes what " +
                 "answers and how well, never the shape of the answer.")]
        public PlannerBackend backend = PlannerBackend.Ollama;

        [Header("Ollama")]
        [Tooltip("Where Ollama is listening, as seen FROM THE HEADSET.\n\n" +
                 "Not localhost. The headset is a separate machine, so this has to be the LAN " +
                 "address of the PC running Ollama, and Ollama has to be listening on more than " +
                 "loopback to accept it (OLLAMA_HOST=0.0.0.0). A wrong address here fails as a " +
                 "connection error, which is the same thing a sleeping PC looks like.")]
        public string ollamaUrl = "http://192.168.1.10:11434";

        [Tooltip("Which local model. It has to be pulled on that machine already -- Ollama does " +
                 "not fetch on demand, it answers with an error naming the model.\n\n" +
                 "Pick an instruct model that follows a schema well rather than the biggest one " +
                 "that fits: this is enumeration and selection, not reasoning.")]
        public string ollamaModel = "qwen2.5:7b";

        [Tooltip("How varied the wording is. Low on purpose -- a plan is instructions, and there " +
                 "is no upside to it phrasing them differently every time you ask.")]
        [Range(0f, 1f)] public float ollamaTemperature = 0.2f;

        [Header("Anthropic")]
        [Tooltip("Which Claude model plans the task.\n\n" +
                 "Haiku 4.5 while the feature is being tried out -- around a fifth the price " +
                 "of Opus 5 per plan, and this is not a hard reasoning problem. If plans come " +
                 "back thin, or steps get grounded to nearly-right places, move up to " +
                 "claude-sonnet-5 or claude-opus-5 and set Effort below.")]
        public string model = "claude-haiku-4-5";

        [Tooltip("How hard the model works before answering. LEAVE THIS EMPTY ON HAIKU 4.5.\n\n" +
                 "Not every model takes this. The 5-class models (opus, sonnet) accept low, " +
                 "medium, high, xhigh and max; Haiku 4.5 rejects the field outright and the " +
                 "whole request fails with a 400. Empty means it is not sent at all, which is " +
                 "the only setting that works everywhere.\n\n" +
                 "On a model that does take it, low is the right starting point: enumerating " +
                 "five grounded steps from a fixed list is not hard, and every extra second is " +
                 "one she stands silent in front of you.")]
        public string effort = "";

        [Header("Both")]
        [Tooltip("Ceiling on the reply, thinking included. A plan is short; this is sized so a " +
                 "long think cannot truncate the JSON halfway through a step.")]
        public int maxTokens = 8000;

        [Tooltip("Seconds before the request is abandoned. Keep this under the Plan Task " +
                 "action's own timeout, or the action gives up first and the answer is wasted.\n\n" +
                 "Sized for the slower of the two: a 7B model on a busy consumer GPU takes tens " +
                 "of seconds where the API takes a few.")]
        public int timeoutSeconds = 40;

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

        /// <summary>How long to wait before looking for a missing key again. See ResolveKey.</summary>
        private const float KeyRetryIntervalSeconds = 5f;

        private string _cachedKey;
        private float _nextKeyLookup;
        private bool _warnedAboutKey;

        /// <summary>
        /// Whether an Anthropic key was found. Only meaningful on that backend.
        /// </summary>
        public bool HasKey => !string.IsNullOrEmpty(ResolveKey());

        /// <summary>
        /// Whether this backend has everything it needs to be asked for a plan. Read by the
        /// health probe, because whatever is missing is invisible from inside the headset and
        /// looks exactly like the feature not working.
        ///
        /// Deliberately NOT "is the server up" for Ollama. Reachability cannot be established
        /// without a round trip, a probe that pings a LAN address every few seconds to colour a
        /// status line is a bad trade, and being unreachable already reports itself the moment
        /// somebody asks for a plan. This answers the cheaper question -- is it configured at
        /// all -- and leaves being wrong about the address to the request that finds out.
        /// </summary>
        public bool IsConfigured =>
            backend == PlannerBackend.Anthropic
                ? HasKey
                : !string.IsNullOrWhiteSpace(ollamaUrl) && !string.IsNullOrWhiteSpace(ollamaModel);

        /// <summary>What the health probe and the panel call this backend.</summary>
        public string BackendName => backend == PlannerBackend.Anthropic ? "anthropic" : "ollama";

        /// <summary>Which model is actually being asked, whichever backend that is.</summary>
        public string ActiveModel => backend == PlannerBackend.Anthropic ? model : ollamaModel;

        /// <summary>
        /// The URL this will actually post to, after the address has been tidied up. Exposed
        /// because a typo'd host is a common way for this to fail, and reading back the address
        /// as the code understood it is faster than reasoning about what the field ought to mean.
        /// </summary>
        public string Endpoint => ResolveEndpoint();

        /// <summary>Plans <paramref name="task"/> against the places in <paramref name="places"/>.</summary>
        public async Task<PlanResult> PlanAsync(
            string task,
            IReadOnlyList<RoomTaskVocabulary.Place> places,
            string roomSummary,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(task))
                return PlanResult.Failed("no task was given");

            // Only Anthropic needs a secret. Asking for one on the Ollama path would refuse to
            // plan over a key nothing is going to send.
            string key = null;

            if (backend == PlannerBackend.Anthropic)
            {
                key = ResolveKey();
                if (string.IsNullOrEmpty(key)) return PlanResult.Failed("no planner API key on this device");
            }
            else if (string.IsNullOrWhiteSpace(ollamaUrl) || string.IsNullOrWhiteSpace(ollamaModel))
            {
                return PlanResult.Failed("the local planner has no address or model set");
            }

            var body = BuildRequest(task, places, roomSummary);

            if (verboseLogging)
                Debug.Log($"{Tag} Planning '{task}' against {places.Count} places " +
                          $"via {BackendName} ({ActiveModel}).");

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
            return backend == PlannerBackend.Anthropic
                ? BuildAnthropicRequest(task, places, roomSummary)
                : BuildOllamaRequest(task, places, roomSummary);
        }

        private string BuildAnthropicRequest(string task, IReadOnlyList<RoomTaskVocabulary.Place> places,
                                             string roomSummary)
        {
            var json = new StringBuilder(2048);

            json.Append('{');
            json.Append("\"model\":").Append(Quote(model)).Append(',');
            json.Append("\"max_tokens\":").Append(Mathf.Max(1024, maxTokens)).Append(',');

            // Effort is omitted entirely when blank rather than sent empty, and that is not
            // tidiness -- it is the difference between a working request and a 400. The field
            // is only understood by the 5-class models; Haiku 4.5 rejects its presence, and it
            // rejects `"effort": ""` just as hard as a real value. The format half is always
            // sent: structured outputs are what make the grounding a guarantee rather than a
            // hope, and every model this would sensibly run on supports them.
            json.Append("\"output_config\":{");

            if (!string.IsNullOrWhiteSpace(effort))
                json.Append("\"effort\":").Append(Quote(effort.Trim())).Append(',');

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
        /// The same request in Ollama's shape.
        ///
        /// Three differences from Anthropic worth naming, because each is a silent failure if
        /// got wrong. The schema goes in `format` BARE -- no json_schema wrapper, no nesting --
        /// and Ollama compiles it to a grammar the sampler is held to. The system prompt is a
        /// message with role system rather than a top-level field. And `stream` must be false:
        /// Ollama streams by default, and a streamed reply arrives as a run of newline-delimited
        /// JSON objects that the parser here would choke on while looking like a malformed
        /// answer rather than a wrong request.
        /// </summary>
        private string BuildOllamaRequest(string task, IReadOnlyList<RoomTaskVocabulary.Place> places,
                                          string roomSummary)
        {
            var json = new StringBuilder(2048);

            json.Append('{');
            json.Append("\"model\":").Append(Quote(ollamaModel.Trim())).Append(',');
            json.Append("\"stream\":false,");

            json.Append("\"format\":");
            AppendSchema(json, places);
            json.Append(',');

            json.Append("\"options\":{");
            json.Append("\"temperature\":").Append(ollamaTemperature.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            json.Append("\"num_predict\":").Append(Mathf.Max(1024, maxTokens));
            json.Append("},");

            json.Append("\"messages\":[");
            json.Append("{\"role\":\"system\",\"content\":")
                .Append(Quote(BuildSystemPrompt(places, roomSummary))).Append("},");
            json.Append("{\"role\":\"user\",\"content\":").Append(Quote(task.Trim())).Append('}');
            json.Append(']');

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

            json.Append("\"where\":{\"type\":\"string\",\"description\":")
                .Append(Quote($"The place in this room where this step happens, chosen from the " +
                              $"listed places. Use \"{NoPlace}\" when no listed place fits."))
                .Append(",\"enum\":[");

            for (var i = 0; i < places.Count; i++)
            {
                json.Append(Quote(places[i].Name)).Append(',');
            }

            // Always last and always present, including when the room has no places at all --
            // an enum with no members is not a schema either backend will accept, and a room
            // that has just been entered with nothing scanned yet is exactly when someone tries
            // this for the first time.
            json.Append(Quote(NoPlace)).Append("]}");

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
                $"the step happens at. Set it to \"{NoPlace}\" when no listed place is where " +
                "that step happens -- a step that is thinking, waiting, or done somewhere this " +
                $"room does not contain. \"{NoPlace}\" is a correct answer and a common one. " +
                "Never pick a nearly-right place to avoid it: a confidently wrong location " +
                "sends the character to the wrong side of the room, which is worse than " +
                "admitting the step has no home here.\n\n");

            if (!string.IsNullOrWhiteSpace(roomSummary))
                prompt.Append("The room: ").Append(roomSummary.Trim()).Append("\n\n");

            if (places.Count == 0)
            {
                prompt.Append(
                    $"This room has no recognised places yet, so `where` must be \"{NoPlace}\" " +
                    "on every step. Say the steps anyway.");

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
            using var request = new UnityWebRequest(ResolveEndpoint(), UnityWebRequest.kHttpVerbPOST);

            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(5, timeoutSeconds);

            request.SetRequestHeader("content-type", "application/json");

            // Only Anthropic authenticates. Ollama is unauthenticated by design -- it expects to
            // be on a network you already trust, which is worth knowing before pointing it at
            // anything beyond your own LAN.
            if (backend == PlannerBackend.Anthropic)
            {
                request.SetRequestHeader("x-api-key", key);
                request.SetRequestHeader("anthropic-version", AnthropicApiVersion);
            }

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

        /// <summary>
        /// Where to post, tolerating however the address was typed.
        ///
        /// The path is appended rather than expected, because the field asks for a host and
        /// somebody will paste a full URL into it sooner or later. Both work, and a doubled
        /// /api/chat would 404 in a way that reads as Ollama being broken.
        /// </summary>
        private string ResolveEndpoint()
        {
            if (backend == PlannerBackend.Anthropic) return AnthropicEndpoint;

            var url = (ollamaUrl ?? "").Trim().TrimEnd('/');

            if (url.Length == 0) return "http://localhost:11434/api/chat";

            // A bare host:port is the common case and needs a scheme before UnityWebRequest
            // will look at it.
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;

            return url.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)
                ? url
                : url + "/api/chat";
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

        /// <summary>
        /// Ollama's reply. The plan is the `content` string on the assistant message, and
        /// `error` is a bare top-level string rather than the object Anthropic returns -- which
        /// is why the two envelopes cannot share a type however similar they look.
        /// </summary>
        [Serializable]
        private class OllamaEnvelope
        {
            public OllamaMessage message;
            public string error;
            public string done_reason;
        }

        [Serializable]
        private class OllamaMessage
        {
            public string role;
            public string content;
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

        /// <summary>The plan text lifted out of whichever envelope carried it, or why it was not.</summary>
        private readonly struct Unwrapped
        {
            public readonly bool Ok;
            public readonly string Text;
            public readonly string Failure;

            private Unwrapped(bool ok, string text, string failure)
            {
                Ok = ok;
                Text = text;
                Failure = failure;
            }

            public static Unwrapped Found(string text) => new Unwrapped(true, text, "");
            public static Unwrapped Failed(string failure) => new Unwrapped(false, "", failure);
        }

        private Unwrapped UnwrapAnthropic(string raw)
        {
            MessageEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<MessageEnvelope>(raw);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read the reply envelope: {ex.Message}");
                return Unwrapped.Failed("the planner's reply was unreadable");
            }

            if (envelope == null) return Unwrapped.Failed("the planner's reply was empty");

            if (envelope.error != null && !string.IsNullOrEmpty(envelope.error.message))
            {
                Debug.LogWarning($"{Tag} The API refused the request: {envelope.error.message}");
                return Unwrapped.Failed(Shorten(envelope.error.message));
            }

            // Structured outputs do not survive a refusal -- the docs are explicit that the
            // reply may not match the schema in that case -- so it is checked before the text is
            // trusted rather than after the parse fails confusingly.
            if (envelope.stop_reason == "refusal")
                return Unwrapped.Failed("the planner declined this task");

            if (envelope.stop_reason == "max_tokens")
                return Unwrapped.Failed("the plan was cut off; raise Max Tokens");

            return Unwrapped.Found(FirstText(envelope));
        }

        /// <summary>
        /// Lifts the plan out of an Ollama reply.
        ///
        /// The error worth reading is almost always the same one -- the model has not been
        /// pulled on that machine -- and Ollama says so in plain words, so it is forwarded
        /// rather than summarised into something less useful.
        /// </summary>
        private Unwrapped UnwrapOllama(string raw)
        {
            OllamaEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<OllamaEnvelope>(raw);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} Could not read the reply envelope: {ex.Message}");
                return Unwrapped.Failed("the planner's reply was unreadable");
            }

            if (envelope == null) return Unwrapped.Failed("the planner's reply was empty");

            if (!string.IsNullOrEmpty(envelope.error))
            {
                Debug.LogWarning($"{Tag} Ollama refused the request: {envelope.error}");
                return Unwrapped.Failed(Shorten(envelope.error));
            }

            // Ollama's own word for hitting num_predict. Worth its own message because the
            // remedy is a setting rather than anything about the room or the request.
            if (envelope.done_reason == "length")
                return Unwrapped.Failed("the plan was cut off; raise Max Tokens");

            return Unwrapped.Found(envelope.message != null ? envelope.message.content : null);
        }

        /// <summary>
        /// Trims anything either side of the JSON object.
        ///
        /// Both backends are told to answer with a schema and both normally do, so this should
        /// be a no-op. It exists for the local case: some models emit a reasoning preamble, or
        /// a &lt;think&gt; block, before the answer they were constrained to produce, and a
        /// grammar that governs the JSON does not always govern what precedes it. Taking the
        /// span from the first brace to the last is enough for every shape of that, and it says
        /// when it did something so this cannot quietly paper over a real malformation.
        /// </summary>
        private string ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var trimmed = text.Trim();
            if (trimmed.Length > 0 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}')
                return trimmed;

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');

            if (start < 0 || end <= start) return trimmed;

            Debug.LogWarning($"{Tag} The reply had {start} characters before the JSON and " +
                             $"{trimmed.Length - end - 1} after it. Trimmed to the object; if " +
                             $"this happens every time, the model is ignoring the schema.");

            return trimmed.Substring(start, end - start + 1);
        }

        private PlanResult Parse(string raw, IReadOnlyList<RoomTaskVocabulary.Place> places)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return PlanResult.Failed("the planner returned nothing");

            // Unwrapping the envelope is the only part of reading a reply that differs between
            // the two. Once the plan text is out, it is the same JSON held to the same schema.
            var unwrapped = backend == PlannerBackend.Anthropic
                ? UnwrapAnthropic(raw)
                : UnwrapOllama(raw);

            if (!unwrapped.Ok) return PlanResult.Failed(unwrapped.Failure);

            var text = ExtractJson(unwrapped.Text);

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

                // The sentinel is turned back into nothing here, so that the whole rest of the
                // project never has to know the wire ever had a word for "nowhere". A step with
                // no place is a step whose Where is null, everywhere above this line.
                var claimed = string.Equals(step.where, NoPlace, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : step.where;

                // A place the room no longer has is dropped rather than kept. The schema enum
                // makes this rare, but a scan reloaded while the request was in flight is a real
                // case, and an unlocated step is honest where a dangling one is not.
                var where = RoomTaskVocabulary.Contains(places, claimed) ? claimed : null;

                if (!string.IsNullOrEmpty(claimed) && where == null)
                {
                    Debug.LogWarning($"{Tag} Dropped the location '{claimed}' from a step -- " +
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
        ///
        /// A key that is FOUND is cached for good; a key that is missing is looked for again on
        /// a slow timer rather than being remembered as absent. Caching the miss is the obvious
        /// thing to write and it produces a genuinely baffling half hour: you push the key file
        /// to the headset, the app is already running, and the panel goes on insisting there is
        /// no key until you work out that the answer was decided seconds after launch. The
        /// retry costs one File.Exists every few seconds, and only while there is no key.
        /// </summary>
        private string ResolveKey()
        {
            if (!string.IsNullOrEmpty(_cachedKey)) return _cachedKey;

            // Unscaled, because this is wall-clock housekeeping and has no business stopping
            // when something pauses the game.
            if (_nextKeyLookup > 0f && Time.unscaledTime < _nextKeyLookup) return null;

            _nextKeyLookup = Time.unscaledTime + KeyRetryIntervalSeconds;

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

            // Once, not every retry. This is polled by the health probe, and a warning on a
            // five-second timer would bury the log it is meant to be read from.
            if (!_warnedAboutKey)
            {
                _warnedAboutKey = true;

                Debug.LogWarning(
                    $"{Tag} No planner key. Put one in Assets/Resources/{KeyResourceName}.txt " +
                    $"(gitignored) before building, or push one to " +
                    $"{Application.persistentDataPath}/{KeyFileName} on the headset -- that one " +
                    $"is picked up within a few seconds, with no restart. Until then she can " +
                    $"still talk about the room but cannot plan anything.");
            }

            _cachedKey = null;
            return null;
        }

        /// <summary>Drops the cached key, so the next lookup starts over. For a key rotated mid-session.</summary>
        public void ForgetKey()
        {
            _cachedKey = null;
            _nextKeyLookup = 0f;
            _warnedAboutKey = false;
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
