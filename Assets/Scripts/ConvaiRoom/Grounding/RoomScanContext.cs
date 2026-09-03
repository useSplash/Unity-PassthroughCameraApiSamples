using System;
using System.Collections.Generic;
using System.Text;
using Convai.Runtime;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.SceneMetadata;
using Convai.Shared.Types;
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
    ///   - ACTION TARGETS, the same objects registered again through the character's own action
    ///     registry. Scene metadata is what she knows about; this is what she can be sent to.
    ///     They are registered under the same names on purpose -- the couch she can describe and
    ///     the couch she can walk to have to be one couch, or "go to the couch" resolves to
    ///     nothing while she is in the middle of telling you about it.
    ///
    /// Everything comes from the scan JSON by way of <see cref="RoomScanRebuilder"/> rather than
    /// re-reading the file, so what she is told and what is drawn in front of you are built from
    /// one parse and can never disagree.
    ///
    /// Walking itself is not driven from here. The prefab's Move To action runs the SDK's own
    /// ConvaiWalkToActionExecutor over the navmesh phase 1 baked; this only supplies the places
    /// it can be pointed at. Nothing in this project decides where she goes.
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

        [Tooltip("Register the same objects as action targets, so she can be asked to walk to " +
                 "them.\n\n" +
                 "Needs the prefab's Move To action to have an executor and the navmesh to be " +
                 "baked. Switched off, she still knows everything in the room and simply cannot " +
                 "be sent anywhere.")]
        public bool makeObjectsWalkable = true;

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
        /// The room in one line -- size, headroom, what is in it -- or empty when no scan is
        /// loaded.
        ///
        /// The same sentence the character is given as dynamic context, exposed so the task
        /// planner can be told it too. Deliberately shared rather than rebuilt: two descriptions
        /// of one room, written by two pieces of code, is how a plan ends up sized for a room
        /// nobody is standing in.
        /// </summary>
        public string RoomSummary
        {
            get
            {
                var scan = rebuilder != null ? rebuilder.Scan : null;
                if (scan == null) return "";

                var size = RoomSize(scan);
                var contents = Contents(scan);

                if (string.IsNullOrEmpty(size)) return contents;
                if (string.IsNullOrEmpty(contents)) return size;

                return $"{size}, containing {contents}";
            }
        }

        /// <summary>
        /// The places this scan would offer a planner, worked out without a scene.
        ///
        /// WHY THIS IS HERE AND NOT IN THE HARNESS. The offline plan corpus is only comparable
        /// to what participants experienced if the planner is offered the same vocabulary they
        /// were, and that vocabulary is not in the scan file -- it is the OUTPUT of the naming
        /// pass below. "chair by the couch" is invented at rebuild time by a greedy
        /// landmark-by-distance assignment under the <see cref="maxObjects"/> cap, and a harness
        /// that re-derived names its own way would produce a corpus about a differently named
        /// room while looking exactly like one about the same room.
        ///
        /// So it runs the real thing. <see cref="Choose"/>, <see cref="NameThem"/> and
        /// <see cref="BuildDescription"/> are the same code the headset runs, and all three turn
        /// out to read only the scan data -- observation counts and room-local positions -- and
        /// never the spawned proxies, which is what makes this possible at all.
        ///
        /// The returned places carry a null Target. Nothing on the planner's request path
        /// touches it: the schema enum is built from Name, and the prompt from Name and
        /// Description. A place with no GameObject is meaningless to the walk executor and fine
        /// here, which is exactly the distinction between planning about a room and standing
        /// in one.
        ///
        /// <paramref name="maxPlaces"/> is the SECOND cap the live path applies and this
        /// replicates it, imperfectly by necessity. Online, RoomTaskPlanner asks
        /// RoomTaskVocabulary.Collect for the nearest <c>maxPlaces</c> targets TO THE PLAYER,
        /// because the room a plan should be about is the one somebody is standing in. Offline
        /// there is no player, so this measures from the room's own centre instead -- the same
        /// anchor every object's description already reports its own distance from. It is a
        /// documented divergence, not a hidden one: a harness that skipped this cap entirely
        /// would hand the planner a bigger vocabulary than any participant's session ever did,
        /// which is exactly the kind of difference that makes a corpus non-representative
        /// without anyone noticing.
        /// </summary>
        public static List<RoomTaskVocabulary.Place> PlacesFor(RoomScanFile scan, int maxObjects,
                                                                int maxPlaces = 0)
        {
            var places = new List<RoomTaskVocabulary.Place>();

            if (scan?.objects == null || scan.objects.Count == 0) return places;

            var rebuilt = new List<RoomScanRebuilder.RebuiltObject>(scan.objects.Count);
            foreach (var data in scan.objects)
            {
                if (data == null) continue;

                // Null proxy on purpose -- see the remark. Everything below reads Data.
                rebuilt.Add(new RoomScanRebuilder.RebuiltObject(data, null));
            }

            var chosen = Choose(rebuilt, maxObjects);
            var names = NameThem(chosen);
            var centre = RoomCentre(scan);

            var indices = new List<int>(chosen.Count);
            for (var i = 0; i < chosen.Count; i++) indices.Add(i);

            // Nearest-to-centre first, only when the cap actually bites -- same guard
            // RoomTaskVocabulary.Collect uses, and for the same reason: sorting is wasted work
            // in the common case where nothing gets trimmed.
            if (maxPlaces > 0 && chosen.Count > maxPlaces)
            {
                indices.Sort((a, b) =>
                {
                    var da = (chosen[a].Data.position.ToVector3() - centre).sqrMagnitude;
                    var db = (chosen[b].Data.position.ToVector3() - centre).sqrMagnitude;
                    return da.CompareTo(db);
                });

                indices.RemoveRange(maxPlaces, indices.Count - maxPlaces);
            }

            foreach (var i in indices)
            {
                places.Add(new RoomTaskVocabulary.Place(
                    names[i].Name,
                    BuildDescription(chosen[i].Data, scan, centre),
                    null));
            }

            return places;
        }

        /// <summary>
        /// The one-line room summary for a scan, without a scene.
        ///
        /// The ungrounded arm of the plan ablation is an empty place list AND a withheld
        /// summary, so the harness needs to be able to produce this one deliberately in order
        /// to deliberately not send it. Shares <see cref="RoomSize"/> and <see cref="Contents"/>
        /// with the property the character is given, for the reason that property gives: two
        /// descriptions of one room, written by two pieces of code, is how a plan ends up sized
        /// for a room nobody is standing in.
        /// </summary>
        public static string SummaryFor(RoomScanFile scan)
        {
            if (scan == null) return "";

            var size = RoomSize(scan);
            var contents = ContentsOf(scan);

            if (string.IsNullOrEmpty(size)) return contents;
            if (string.IsNullOrEmpty(contents)) return size;

            return $"{size}, containing {contents}";
        }

        /// <summary>
        /// Finds the box she knows by this name, matching aliases as well as primary names.
        ///
        /// Aliases are the reason this exists. Every repeated object carries its number as an
        /// alias -- "chair 2" alongside "chair by the couch" -- because the numbers are what the
        /// panel and the logs show, and a name you can read but not say would be worse than the
        /// spatial one. The Convai backend resolves those aliases; nothing in Unity did.
        /// RoomTaskVocabulary.Contains compares Place.Name only, so a participant who said
        /// "chair 2" got an answer from the character and no match at all on this side. Every
        /// caller that needs to turn a spoken name back into an object had the same hole.
        ///
        /// Read off the ConvaiActionTarget components rather than from <see cref="_described"/>,
        /// so this answers for exactly the set the backend was offered -- the aliases live on
        /// the target and nowhere else, and a second copy of that mapping here is one that goes
        /// stale the first time MakeWalkable changes.
        ///
        /// Case- and whitespace-insensitive: this matches against speech that has been through
        /// a transcriber, not against an identifier.
        /// </summary>
        public bool TryResolve(string nameOrAlias, out GameObject proxy)
        {
            proxy = null;

            if (string.IsNullOrWhiteSpace(nameOrAlias) || rebuilder == null) return false;

            var wanted = nameOrAlias.Trim();

            foreach (var entry in rebuilder.Rebuilt)
            {
                if (entry.Proxy == null) continue;
                if (!entry.Proxy.TryGetComponent<ConvaiActionTarget>(out var target)) continue;

                if (Matches(target.TargetName, wanted))
                {
                    proxy = entry.Proxy;
                    return true;
                }

                if (target.Aliases == null) continue;

                foreach (var alias in target.Aliases)
                {
                    if (!Matches(alias, wanted)) continue;

                    proxy = entry.Proxy;
                    return true;
                }
            }

            return false;
        }

        private static bool Matches(string candidate, string wanted) =>
            !string.IsNullOrEmpty(candidate) &&
            string.Equals(candidate.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

        /// <summary>One scanned object as the character has been told about it.</summary>
        private readonly struct Described
        {
            /// <summary>What she calls it. Unique within the scan -- see NameThem.</summary>
            public readonly string Name;

            /// <summary>The raw scan label, for counting kinds in the room summary.</summary>
            public readonly string Label;

            public readonly string Description;

            /// <summary>The replayed box, which is the thing she actually walks to.</summary>
            public readonly GameObject Proxy;

            public Described(string name, string label, string description, GameObject proxy)
            {
                Name = name;
                Label = label;
                Description = description;
                Proxy = proxy;
            }
        }

        /// <summary>
        /// What she has been told about, in the order it was described.
        ///
        /// Kept rather than recomputed, because three separate things have to agree about it: the
        /// world objects, the room summary's counts, and the action targets. Working the set out
        /// again from the scan file for any one of them would have her told "there are six chairs"
        /// while only four exist as things she can name, any time the cap trims the list.
        /// </summary>
        private readonly List<Described> _described = new List<Described>();


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
            _described.Clear();

            if (!describeObjects || source == null || source.Scan == null) return;

            // No cleanup pass for the previous scan's objects. Rebuild destroys every proxy it
            // spawned, and ConvaiObjectMetadata unregisters itself in OnDestroy, so the old set
            // has already left the registry by the time this runs.
            var chosen = Choose(source.Rebuilt, maxObjects);
            var names = NameThem(chosen);

            var centre = RoomCentre(source.Scan);

            for (var i = 0; i < chosen.Count; i++)
            {
                var entry = chosen[i];
                if (entry.Proxy == null) continue;

                var description = Describe(entry.Proxy, entry.Data, source.Scan, centre, names[i]);
                _described.Add(new Described(names[i].Name, Label(entry.Data), description, entry.Proxy));
                DescribedCount++;
            }

            if (verboseLogging)
            {
                var described = new List<string>(_described.Count);
                foreach (var entry in _described) described.Add(entry.Name);

                Debug.Log($"{Tag} Described {DescribedCount} of {source.Rebuilt.Count} scanned " +
                          $"objects to Convai: {string.Join(", ", described)}");
            }

            // A scan can be re-loaded with the character already standing there. The world
            // objects and the walk targets both re-sync on their own -- the SDK polls one and
            // watches the other -- but the room summary is ours to re-send, and a stale one would
            // have her describing the room she was told about rather than the one now around her.
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
        private static List<RoomScanRebuilder.RebuiltObject> Choose(
            IReadOnlyList<RoomScanRebuilder.RebuiltObject> rebuilt, int maxObjects)
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

        /// <summary>A name for one object, plus the other names it will still answer to.</summary>
        private readonly struct Naming
        {
            public readonly string Name;
            public readonly List<string> Aliases;

            public Naming(string name, List<string> aliases)
            {
                Name = name;
                Aliases = aliases;
            }
        }

        /// <summary>
        /// Gives every object a name worth saying out loud.
        ///
        /// Labels repeat -- a room has four chairs -- and numbering them "chair 1" through
        /// "chair 4" is unusable in conversation twice over: you cannot say which one you mean,
        /// and neither can she. The numbers are an index into a list only the code can see.
        ///
        /// So a repeated object is named after the nearest thing that is one of a kind: "chair by
        /// the couch". That is a name both ends of the conversation can resolve by looking. The
        /// landmark has to be unique itself, or "the chair by the chair" is no better than a
        /// number. Where the landmarks run out -- four chairs and nothing else in the room --
        /// the remainder fall back to numbering, which is at least still unambiguous.
        ///
        /// The number survives as an ALIAS on every repeated object, so "chair 2" keeps working
        /// even once the primary name is spatial. That matters because the numbers are what the
        /// panel and the logs show, and a name you can read but not say would be worse than the
        /// one this replaces.
        ///
        /// A lone object of its kind keeps the bare label. "chair 1" when there is only one chair
        /// reads like there is a chair 2 somewhere.
        /// </summary>
        private static List<Naming> NameThem(List<RoomScanRebuilder.RebuiltObject> chosen)
        {
            var totals = new Dictionary<string, int>();
            foreach (var entry in chosen)
            {
                var label = Label(entry.Data);
                totals.TryGetValue(label, out var count);
                totals[label] = count + 1;
            }

            // Ordinals first and independently of the spatial pass, so an object's number is its
            // position among its own kind in file order -- stable, and the same number the panel
            // and the logs will show whether or not a landmark was found for it.
            var ordinals = new int[chosen.Count];
            var seen = new Dictionary<string, int>();
            for (var i = 0; i < chosen.Count; i++)
            {
                var label = Label(chosen[i].Data);
                seen.TryGetValue(label, out var index);
                index++;
                seen[label] = index;
                ordinals[i] = index;
            }

            var landmarks = LandmarkIndices(chosen, totals);
            var spatial = AssignLandmarks(chosen, totals, landmarks);

            var result = new List<Naming>(chosen.Count);
            for (var i = 0; i < chosen.Count; i++)
            {
                var label = Label(chosen[i].Data);

                if (totals[label] == 1)
                {
                    result.Add(new Naming(Clamp(label, MaxNameLength), new List<string>()));
                    continue;
                }

                var numbered = $"{label} {ordinals[i]}";
                var name = spatial[i] != null
                    ? Clamp($"{label} by the {spatial[i]}", MaxNameLength)
                    : Clamp(numbered, MaxNameLength);

                // Never alias a name to itself -- the fallback case has already used the number
                // as the primary name, and a duplicate entry in the ladder is just noise.
                var aliases = new List<string>();
                if (name != numbered) aliases.Add(Clamp(numbered, MaxNameLength));

                result.Add(new Naming(name, aliases));
            }

            return result;
        }

        /// <summary>
        /// Which objects are fit to name others by: the ones whose label occurs exactly once, so
        /// saying "by the couch" points at one place rather than at a category.
        /// </summary>
        private static List<int> LandmarkIndices(
            List<RoomScanRebuilder.RebuiltObject> chosen,
            Dictionary<string, int> totals)
        {
            var landmarks = new List<int>();
            for (var i = 0; i < chosen.Count; i++)
                if (totals[Label(chosen[i].Data)] == 1) landmarks.Add(i);

            return landmarks;
        }

        /// <summary>
        /// Pairs each repeated object with its own landmark, closest pairs first.
        ///
        /// Greedy over every candidate pair sorted by distance, rather than each object simply
        /// taking its nearest landmark: two chairs either side of one couch would both be "the
        /// chair by the couch", which is the exact ambiguity this is here to remove. Claiming a
        /// landmark takes it out of the running for the rest of its group, so the second chair
        /// moves on to the next nearest thing -- or to a number if the room has nothing else.
        /// </summary>
        private static string[] AssignLandmarks(
            List<RoomScanRebuilder.RebuiltObject> chosen,
            Dictionary<string, int> totals,
            List<int> landmarks)
        {
            var assigned = new string[chosen.Count];
            if (landmarks.Count == 0) return assigned;

            var pairs = new List<(int Member, int Landmark, float Distance)>();
            for (var i = 0; i < chosen.Count; i++)
            {
                if (totals[Label(chosen[i].Data)] == 1) continue;

                var from = chosen[i].Data.position.ToVector3();
                foreach (var landmark in landmarks)
                {
                    var to = chosen[landmark].Data.position.ToVector3();
                    pairs.Add((i, landmark, Vector3.Distance(from, to)));
                }
            }

            pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            // Claimed per GROUP, not globally: a couch can be the landmark for a chair and for a
            // lamp at the same time without either becoming ambiguous, because the label in front
            // of "by the couch" already tells them apart.
            var claimed = new HashSet<string>();
            foreach (var pair in pairs)
            {
                if (assigned[pair.Member] != null) continue;

                var key = Label(chosen[pair.Member].Data) + "|" + Label(chosen[pair.Landmark].Data);
                if (!claimed.Add(key)) continue;

                assigned[pair.Member] = Label(chosen[pair.Landmark].Data);
            }

            return assigned;
        }

        private string Describe(GameObject proxy, ScannedObject data, RoomScanFile scan,
                                Vector3 centre, Naming naming)
        {
            if (!proxy.TryGetComponent<ConvaiObjectMetadata>(out var metadata))
                metadata = proxy.AddComponent<ConvaiObjectMetadata>();

            // Assigned through the properties rather than at AddComponent time, and it matters:
            // the component registers itself in OnEnable, which AddComponent runs immediately
            // and therefore before there is a name to register. The setters mark the registry
            // dirty, which is what gets the finished entry re-sent to a character who is
            // already connected.
            var description = BuildDescription(data, scan, centre);

            metadata.ObjectName = naming.Name;
            metadata.ObjectDescription = description;
            metadata.IncludeInMetadata = true;

            MakeWalkable(proxy, naming, description);

            return description;
        }

        /// <summary>
        /// Makes the object somewhere she can be sent, under the same name she describes it by.
        ///
        /// A ConvaiActionTarget component rather than a call to Actions.RegisterObject, for two
        /// reasons. It carries aliases, which the register call has no parameter for and which
        /// are what keep "chair 2" working alongside "chair by the couch". And it is POLLED by
        /// each character's config builder rather than pushed into one character's registry, so
        /// it needs no bookkeeping across a respawn: the target belongs to the box, dies with the
        /// box when the scan is reloaded, and is picked up by whichever character is standing
        /// there without anything having to re-register it.
        /// </summary>
        private void MakeWalkable(GameObject proxy, Naming naming, string description)
        {
            if (!proxy.TryGetComponent<ConvaiActionTarget>(out var target))
            {
                // Nothing to switch off if there is nothing there, and no reason to add a
                // component only to disable it.
                if (!makeObjectsWalkable) return;
                target = proxy.AddComponent<ConvaiActionTarget>();
            }

            target.TargetName = naming.Name;
            target.Description = description;
            target.Aliases = naming.Aliases;
            target.Kind = ConvaiActionTargetKind.Object;

            // The component registers on enable and withdraws on disable, so this is the whole
            // implementation of the toggle -- including switching it off mid-session.
            target.enabled = makeObjectsWalkable;
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

            // The described set when there is one, so the summary and the world objects agree.
            // Falls back to the raw scan only when object descriptions are switched off, where
            // there is nothing to agree with and the file is the best answer available.
            if (_described.Count == 0) return ContentsOf(scan);

            var totals = new Dictionary<string, int>();
            var order = new List<string>();

            foreach (var entry in _described) Tally(totals, order, entry.Label);

            return Phrase(totals, order);
        }

        /// <summary>
        /// The same count taken from the scan file alone, for callers with no scene.
        ///
        /// Split out rather than duplicated in the harness: this sentence goes into the planner
        /// prompt on the grounded arm, and a second implementation of it would be a second thing
        /// the offline corpus could differ from the headset by.
        /// </summary>
        private static string ContentsOf(RoomScanFile scan)
        {
            if (scan?.objects == null || scan.objects.Count == 0)
                return "nothing was picked up in the scan";

            var totals = new Dictionary<string, int>();
            var order = new List<string>();

            foreach (var obj in scan.objects) Tally(totals, order, Label(obj));

            return Phrase(totals, order);
        }

        private static string Phrase(Dictionary<string, int> totals, List<string> order)
        {
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
