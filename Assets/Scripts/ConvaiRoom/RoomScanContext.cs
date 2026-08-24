using System.Collections.Generic;
using System.Text;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.SceneMetadata;
using RoomScan;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Tells the character what the scan found, so she can talk about the room she is standing
    /// in rather than a generic one.
    ///
    /// Convai has two runtime surfaces for this and they do different jobs, so both are used:
    ///
    ///   - SCENE METADATA, one <see cref="ConvaiObjectMetadata"/> per scanned object. These are
    ///     the "world objects" the character is told exist: a name she can say back to you and a
    ///     description she can reason about. They register themselves into a global registry the
    ///     moment they are added, and the SDK re-sends the whole set whenever it changes, so
    ///     objects can appear and disappear mid-session -- which is exactly what re-loading a
    ///     scan does.
    ///
    ///   - DYNAMIC CONTEXT, a handful of room-level facts pushed onto the character herself.
    ///     Scene metadata is a list of things; it has nowhere to put "the room is 4 by 3 metres
    ///     and has two chairs in it", and that summary is what makes her sound like she is
    ///     actually in the room rather than reciting an inventory.
    ///
    /// Everything comes from the scan JSON by way of <see cref="RoomScanRebuilder"/> rather than
    /// re-reading the file, so what she is told and what is drawn in front of you are built from
    /// one parse and can never disagree.
    ///
    /// Nothing here makes her ACT on any of it. These objects are described, not registered as
    /// action targets, so "walk to the couch" is still not a thing she can do -- see
    /// ConvaiCharacterActions.RegisterObject for that, and note the Move To and Look At actions
    /// on the prefab have no executor yet.
    /// </summary>
    public class RoomScanContext : MonoBehaviour
    {
        private const string Tag = "[RoomContext]";

        /// <summary>
        /// The SDK's own limits, from ConvaiObjectMetadata.GetValidationErrors. Exceeding either
        /// is a warning per object on a component we add dozens of, so the text is built to fit
        /// rather than trimmed after the fact.
        /// </summary>
        private const int MaxNameLength = 50;
        private const int MaxDescriptionLength = 200;

        [Header("Wiring (left empty, this is found in the scene)")]
        [Tooltip("Where the scan comes from. This follows its rebuild event, so re-loading a " +
                 "scan re-describes the room without anything having to ask.")]
        public RoomScanRebuilder rebuilder;

        [Tooltip("Says when the character is connected and ready. Room-level facts are pushed " +
                 "then, because the SDK drops context updates made before she is in conversation.")]
        public RoomCharacterVoice voice;

        [Header("What to tell her")]
        [Tooltip("Describe each scanned object as a Convai world object she can name and " +
                 "reason about.")]
        public bool describeObjects = true;

        [Tooltip("Push the room summary -- size, ceiling height, what is in it -- as dynamic " +
                 "context when she connects.")]
        public bool describeRoom = true;

        [Tooltip("Most objects to describe. A scan of a cluttered room can run to hundreds of " +
                 "boxes, and every one of them is context the model has to read on every turn. " +
                 "Over this, the best-observed survive -- a cluster seen forty times is a real " +
                 "piece of furniture, one seen three times is often a reflection.")]
        public int maxObjects = 40;

        [Tooltip("Let her remark on the room unprompted when she arrives.\n\n" +
                 "Off by default. On, the room summary lands as a conversation event she may " +
                 "answer out loud, which demos well but means she talks first; off, she simply " +
                 "knows the room and waits for you.")]
        public bool remarkOnArrival;

        [Header("Debug")]
        public bool verboseLogging = true;

        /// <summary>How many objects she has been told about. Read by the panel.</summary>
        public int DescribedCount { get; private set; }

        /// <summary>
        /// The labels of the objects actually described, in the order they were described.
        ///
        /// Kept rather than recomputed because the room summary has to agree with the world
        /// objects exactly: working the counts out a second time from the scan file would have
        /// her told "there are six chairs" while only four of them exist as things she can name,
        /// any time the cap trims the list.
        /// </summary>
        private readonly List<string> _describedLabels = new List<string>();

        private void Awake()
        {
            if (rebuilder == null) rebuilder = FindAnyObjectByType<RoomScanRebuilder>();
            if (voice == null) voice = FindAnyObjectByType<RoomCharacterVoice>();

            if (rebuilder == null)
                Debug.LogError($"{Tag} No RoomScanRebuilder in the scene, so there is no scan to " +
                               $"describe and she will know nothing about the room.", this);
        }

        private void OnEnable()
        {
            if (rebuilder != null)
            {
                rebuilder.OnRebuilt += HandleRebuilt;

                // A scan may already be replayed by the time this switches on. The rebuild
                // event has been and gone in that case, and without this the room would stay
                // undescribed until someone pressed LOAD SAVED SCAN again.
                if (rebuilder.Scan != null) HandleRebuilt(rebuilder);
            }

            if (voice != null) voice.OnReady += HandleCharacterReady;
        }

        private void OnDisable()
        {
            if (rebuilder != null) rebuilder.OnRebuilt -= HandleRebuilt;
            if (voice != null) voice.OnReady -= HandleCharacterReady;
        }

        // -----------------------------------------------------------------
        // Objects
        // -----------------------------------------------------------------

        private void HandleRebuilt(RoomScanRebuilder source)
        {
            DescribedCount = 0;
            _describedLabels.Clear();

            if (!describeObjects || source == null || source.Scan == null) return;

            // No cleanup pass for the previous scan's objects. Rebuild destroys every proxy it
            // spawned, and ConvaiObjectMetadata unregisters itself in OnDestroy, so the old set
            // has already left the registry by the time this runs.
            var chosen = Choose(source.Rebuilt);
            var names = NameThem(chosen);

            var centre = RoomCentre(source.Scan);

            for (var i = 0; i < chosen.Count; i++)
            {
                var entry = chosen[i];
                if (entry.Proxy == null) continue;

                Describe(entry.Proxy, entry.Data, source.Scan, centre, names[i]);
                _describedLabels.Add(Label(entry.Data));
                DescribedCount++;
            }

            if (verboseLogging)
                Debug.Log($"{Tag} Described {DescribedCount} of {source.Rebuilt.Count} scanned " +
                          $"objects to Convai.");

            // A scan can be re-loaded with the character already standing there. The world
            // objects re-sync on their own -- the SDK watches its registry -- but the room
            // summary is ours to re-send, and a stale one would have her describing the room
            // she was told about rather than the one now drawn around her.
            if (voice != null && voice.State == RoomCharacterVoice.VoiceState.Ready)
                PushRoomFacts(voice.Character, announce: false);
        }

        /// <summary>
        /// Picks which objects are worth the context budget, keeping file order.
        ///
        /// Trimming by observation count rather than confidence on purpose: confidence is YOLO's
        /// opinion of one frame, while observations is how many separate frames agreed the thing
        /// was there. A poster of a dog scores high confidence every time and is still not a dog;
        /// a couch seen from forty angles is a couch.
        /// </summary>
        private List<RoomScanRebuilder.RebuiltObject> Choose(
            IReadOnlyList<RoomScanRebuilder.RebuiltObject> rebuilt)
        {
            var all = new List<RoomScanRebuilder.RebuiltObject>(rebuilt.Count);
            foreach (var entry in rebuilt)
                if (entry.Data != null) all.Add(entry);

            if (maxObjects <= 0 || all.Count <= maxObjects) return all;

            // Sorted, cut, then put back into file order. Names are assigned from this list and
            // "chair 1, chair 2" reading in the order they sit in the file is easier to reason
            // about than in order of how well they were seen.
            //
            // Ranked by POSITION rather than by the object's id, so the cut is exact whatever
            // the file holds -- ids are not guaranteed unique across older scans, and a set
            // keyed on them would quietly keep every object that shares one.
            var ranked = new List<int>(all.Count);
            for (var i = 0; i < all.Count; i++) ranked.Add(i);
            ranked.Sort((a, b) => all[b].Data.observations.CompareTo(all[a].Data.observations));

            var kept = new HashSet<int>();
            for (var i = 0; i < maxObjects; i++) kept.Add(ranked[i]);

            var result = new List<RoomScanRebuilder.RebuiltObject>(maxObjects);
            for (var i = 0; i < all.Count; i++)
                if (kept.Contains(i)) result.Add(all[i]);

            Debug.LogWarning($"{Tag} The scan holds {all.Count} objects, over the {maxObjects} " +
                             $"cap. Kept the {maxObjects} best-observed; the rest are drawn in " +
                             $"the room but she has not been told about them.");

            return result;
        }

        /// <summary>
        /// Gives every object a name she can say back to you.
        ///
        /// Labels repeat -- a room has four chairs -- and four world objects all called "chair"
        /// leaves her unable to tell you which one she means, or to tell them apart at all.
        /// A lone object of its kind keeps the bare label, because "chair 1" when there is only
        /// one chair reads like there is a chair 2 somewhere.
        /// </summary>
        private static List<string> NameThem(List<RoomScanRebuilder.RebuiltObject> chosen)
        {
            var totals = new Dictionary<string, int>();
            foreach (var entry in chosen)
            {
                var label = Label(entry.Data);
                totals.TryGetValue(label, out var count);
                totals[label] = count + 1;
            }

            var seen = new Dictionary<string, int>();
            var names = new List<string>(chosen.Count);

            foreach (var entry in chosen)
            {
                var label = Label(entry.Data);

                if (totals[label] == 1)
                {
                    names.Add(Clamp(label, MaxNameLength));
                    continue;
                }

                seen.TryGetValue(label, out var index);
                index++;
                seen[label] = index;

                names.Add(Clamp($"{label} {index}", MaxNameLength));
            }

            return names;
        }

        private void Describe(GameObject proxy, ScannedObject data, RoomScanFile scan,
                              Vector3 centre, string name)
        {
            if (!proxy.TryGetComponent<ConvaiObjectMetadata>(out var metadata))
                metadata = proxy.AddComponent<ConvaiObjectMetadata>();

            // Assigned through the properties rather than at AddComponent time, and it matters:
            // the component registers itself in OnEnable, which AddComponent runs immediately
            // and therefore before there is a name to register. The setters mark the registry
            // dirty, which is what gets the finished entry re-sent to a character who is
            // already connected.
            metadata.ObjectName = name;
            metadata.ObjectDescription = BuildDescription(data, scan, centre);
            metadata.IncludeInMetadata = true;
        }

        /// <summary>
        /// Writes the sentence the model actually reads about one object.
        ///
        /// Every fact here is one the scan genuinely knows. Deliberately absent is anything
        /// about where the object sits relative to YOU -- the scan is room-local and static
        /// while you walk around, so "on your left" would be true for about four seconds and
        /// wrong afterwards, and a confidently wrong direction is worse than no direction.
        /// Distance from the middle of the room is the strongest claim that stays true.
        /// </summary>
        private static string BuildDescription(ScannedObject data, RoomScanFile scan, Vector3 centre)
        {
            var size = data.size.ToVector3();
            var position = data.position.ToVector3();

            var builder = new StringBuilder();
            builder.Append("A ").Append(Label(data)).Append(" the headset scanned in this room. ");
            builder.Append("Roughly ").Append(Metres(size.x)).Append(" by ").Append(Metres(size.z))
                   .Append(" and ").Append(Metres(size.y)).Append(" tall");

            var floor = scan?.room != null ? scan.room.floorY : 0f;
            var underside = position.y - size.y * 0.5f - floor;

            // A 15 cm allowance rather than an exact test. Boxes from a depth scan routinely
            // clip through the floor plane by a few centimetres, and calling a chair "raised
            // 0.04 m off the floor" is both wrong and strange to hear said out loud.
            builder.Append(underside > 0.15f
                ? $", standing {Metres(underside)} off the floor"
                : ", standing on the floor");

            var offset = new Vector2(position.x - centre.x, position.z - centre.z).magnitude;
            builder.Append(", about ").Append(Metres(offset)).Append(" from the middle of the room.");

            return Clamp(builder.ToString(), MaxDescriptionLength);
        }

        // -----------------------------------------------------------------
        // The room itself
        // -----------------------------------------------------------------

        private void HandleCharacterReady(ConvaiCharacter character) =>
            PushRoomFacts(character, announce: remarkOnArrival);

        private void PushRoomFacts(ConvaiCharacter character, bool announce)
        {
            if (!describeRoom || character == null) return;

            var scan = rebuilder != null ? rebuilder.Scan : null;

            if (scan == null)
            {
                Debug.LogWarning($"{Tag} No scan is loaded, so she has been told nothing about " +
                                 $"the room she is standing in.");
                return;
            }

            var facts = new Dictionary<string, string>();

            var size = RoomSize(scan);
            if (!string.IsNullOrEmpty(size)) facts["room.size"] = size;

            var contents = Contents(scan);
            if (!string.IsNullOrEmpty(contents)) facts["room.contents"] = contents;

            if (facts.Count == 0) return;

            // Silent: this is awareness, not news. She should know the room the way you know
            // the room you walked into, which is to say without announcing it.
            character.DynamicContext.SetStates(facts, ConvaiRespondMode.Silent);

            // The one thing that IS news, and only when asked for. Auto rather than
            // MustRespond so she can still judge it not worth saying -- being made to comment
            // on the furniture the instant she arrives is worse than her choosing not to.
            if (announce)
                character.DynamicContext.AddEvent(
                    "You have just arrived in this room and can see it for the first time.",
                    ConvaiRespondMode.Auto);

            if (verboseLogging)
                Debug.Log($"{Tag} Told '{character.CharacterName}' about the room: " +
                          $"{string.Join(" | ", facts.Values)}");
        }

        /// <summary>
        /// The room's footprint and headroom, or empty when the scan never recorded walls.
        ///
        /// Returns nothing rather than guessing from where the furniture happens to sit. A
        /// bounding box around four objects in the middle of a large room describes the
        /// furniture, not the room, and stating it as the room's size would have her confidently
        /// telling you your living room is two metres across.
        /// </summary>
        private static string RoomSize(RoomScanFile scan)
        {
            var height = scan.room != null ? scan.room.ceilingY - scan.room.floorY : 0f;

            if (!TryWallBounds(scan, out var bounds))
            {
                return height > 0.5f
                    ? $"the ceiling is about {Metres(height)} up"
                    : "";
            }

            var footprint = $"about {Metres(bounds.size.x)} by {Metres(bounds.size.z)}";

            return height > 0.5f
                ? $"{footprint}, with a ceiling about {Metres(height)} up"
                : footprint;
        }

        /// <summary>What is in the room, counted by kind: "2 chairs, a couch and a tv".</summary>
        private string Contents(RoomScanFile scan)
        {
            if (scan.objects == null || scan.objects.Count == 0)
                return "nothing was picked up in the scan";

            var totals = new Dictionary<string, int>();
            var order = new List<string>();

            // The described set when there is one, so the summary and the world objects agree.
            // Falls back to the raw scan only when object descriptions are switched off, where
            // there is nothing to agree with and the file is the best answer available.
            if (_describedLabels.Count > 0)
            {
                foreach (var label in _describedLabels) Tally(totals, order, label);
            }
            else
            {
                foreach (var obj in scan.objects) Tally(totals, order, Label(obj));
            }

            var parts = new List<string>(order.Count);
            foreach (var label in order)
            {
                var count = totals[label];
                parts.Add(count == 1 ? Article(label) : $"{count} {Plural(label)}");
            }

            return Join(parts);
        }

        private static void Tally(Dictionary<string, int> totals, List<string> order, string label)
        {
            if (!totals.TryGetValue(label, out var count)) order.Add(label);
            totals[label] = count + 1;
        }

        /// <summary>
        /// The room's extents, taken from the saved wall centres.
        ///
        /// Wall centres rather than wall corners because that is what the scan stores, and it is
        /// close enough: every centre lies on its own wall, so the box around them is the room's
        /// footprint to within half a wall's thickness. Three walls is the floor for trusting it
        /// -- two opposite walls give a width and no depth at all.
        /// </summary>
        private static bool TryWallBounds(RoomScanFile scan, out Bounds bounds)
        {
            bounds = default;

            var walls = scan?.room?.walls;
            if (walls == null || walls.Count < 3) return false;

            bounds = new Bounds(walls[0].center.ToVector3(), Vector3.zero);
            for (var i = 1; i < walls.Count; i++)
                bounds.Encapsulate(walls[i].center.ToVector3());

            return true;
        }

        /// <summary>
        /// The point every object's distance is quoted against. The walls when they were saved,
        /// otherwise the middle of the furniture -- which is a weaker centre but still gives
        /// distances that are consistent with each other, which is most of what they are for.
        /// </summary>
        private static Vector3 RoomCentre(RoomScanFile scan)
        {
            if (TryWallBounds(scan, out var walls)) return walls.center;

            if (scan?.objects == null || scan.objects.Count == 0) return Vector3.zero;

            var bounds = new Bounds(scan.objects[0].position.ToVector3(), Vector3.zero);
            for (var i = 1; i < scan.objects.Count; i++)
                bounds.Encapsulate(scan.objects[i].position.ToVector3());

            return bounds.center;
        }

        // -----------------------------------------------------------------
        // Words
        // -----------------------------------------------------------------

        private static string Label(ScannedObject data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.label)) return "object";
            return data.label.Trim();
        }

        /// <summary>Metres, to the nearest centimetre, without trailing zeros.</summary>
        private static string Metres(float value) => $"{Mathf.Abs(value):0.##} m";

        private static string Article(string label) =>
            "aeiou".IndexOf(char.ToLowerInvariant(label[0])) >= 0 ? $"an {label}" : $"a {label}";

        /// <summary>
        /// Naive pluralisation, with the one rule that matters here.
        ///
        /// The labels are COCO class names, which are ordinary lowercase nouns, and a bare "s"
        /// handles nearly all of them. The sibilants are the exception worth catching because
        /// "couch" is in the set and "2 couchs" is the kind of wrongness that makes a character
        /// sound broken rather than approximate.
        /// </summary>
        private static string Plural(string label)
        {
            if (label.EndsWith("s") || label.EndsWith("x") || label.EndsWith("z") ||
                label.EndsWith("ch") || label.EndsWith("sh"))
                return label + "es";

            return label + "s";
        }

        /// <summary>"a, b and c" -- read aloud, which is what this ends up being.</summary>
        private static string Join(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];

            var head = string.Join(", ", parts.GetRange(0, parts.Count - 1));
            return $"{head} and {parts[parts.Count - 1]}";
        }

        private static string Clamp(string text, int limit) =>
            string.IsNullOrEmpty(text) || text.Length <= limit ? text : text.Substring(0, limit);
    }
}
