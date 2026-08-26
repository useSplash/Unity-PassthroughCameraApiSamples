using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// The correction that carries a saved scan's room-local poses into the room-local frame
    /// MRUK is reporting right now. <see cref="Apply(Vector3)"/> is a no-op when nothing was
    /// solved, so callers can apply it unconditionally.
    /// </summary>
    public readonly struct RoomAlignment
    {
        /// <summary>False when the poses were left exactly as the file stored them.</summary>
        public readonly bool Applied;

        public readonly float YawDegrees;
        public readonly Vector3 Translation;

        /// <summary>Mean distance from each saved wall to the current wall it matched, metres.</summary>
        public readonly float Error;

        /// <summary>
        /// How much worse the runner-up hypothesis scored. Small means two different
        /// orientations fit the room about equally well -- see <see cref="Ambiguous"/>.
        /// </summary>
        public readonly float Margin;

        /// <summary>
        /// The room's walls do not pin down a single orientation. A rectangular room maps onto
        /// itself when turned 180 degrees, and a square one every 90, so the walls alone cannot
        /// tell those apart. The fit still gets applied -- the alternative is leaving the scan
        /// in a frame we already know is stale -- but it is worth saying out loud.
        /// </summary>
        public readonly bool Ambiguous;

        public readonly string Summary;

        private readonly Quaternion _rotation;

        public RoomAlignment(bool applied, float yawDegrees, Vector3 translation,
                             float error, float margin, bool ambiguous, string summary)
        {
            Applied = applied;
            YawDegrees = yawDegrees;
            Translation = translation;
            Error = error;
            Margin = margin;
            Ambiguous = ambiguous;
            Summary = summary;
            _rotation = Quaternion.AngleAxis(yawDegrees, Vector3.up);
        }

        public static RoomAlignment None(string why)
            => new RoomAlignment(false, 0f, Vector3.zero, 0f, 0f, false, why);

        public Vector3 Apply(Vector3 local) => Applied ? _rotation * local + Translation : local;

        public Quaternion Apply(Quaternion local) => Applied ? _rotation * local : local;
    }

    /// <summary>
    /// Fits the walls a scan was captured against onto the walls MRUK reports now.
    ///
    /// Why this is needed at all: object poses are stored relative to the MRUK room anchor, so
    /// the same room with the same Space Setup replays correctly with no help from here. Re-run
    /// Space Setup, though, and you get a new room anchor whose origin and heading need not
    /// match the old one -- same physical room, same furniture, different frame. Every saved
    /// pose is then off by exactly one rigid transform, which is what this recovers.
    ///
    /// Rooms do not tilt, so the search is deliberately only yaw plus translation. Solving a
    /// full 6-DOF fit would let a noisy wall rotate the whole room out of level to shave a
    /// centimetre off the error, which looks far worse than the error it removes.
    ///
    /// Assumes it is the same room. Nothing here tries to decide whether the scan belongs to
    /// this room in the first place -- it reports the fit error and lets the caller judge.
    /// </summary>
    public static class RoomScanAligner
    {
        /// <summary>A wall reduced to what the fit actually uses: where it is and which way it faces.</summary>
        private readonly struct Wall
        {
            public readonly Vector3 Center;   // room-local
            public readonly Vector3 Normal;   // room-local, flattened to XZ and normalized
            public readonly float Width;

            public Wall(Vector3 center, Vector3 normal, float width)
            {
                Center = center;
                Normal = normal;
                Width = width;
            }
        }

        /// <summary>Beyond this, a saved wall is treated as having found no partner at all.</summary>
        private const float UnmatchedPenalty = 2f;

        /// <summary>How far two walls may differ in facing and still be considered the same wall.</summary>
        private const float NormalToleranceDegrees = 30f;

        /// <summary>
        /// Solves for the correction, or returns <see cref="RoomAlignment.None"/> with a reason.
        /// </summary>
        /// <param name="maxError">
        /// Mean wall error above which the fit is rejected and the file's own frame is kept.
        /// A bad fit is worse than no fit: it moves every object confidently to the wrong place.
        /// </param>
        /// <param name="ambiguityMargin">
        /// How much better the winner must score than the runner-up before the orientation is
        /// considered settled.
        /// </param>
        public static RoomAlignment Solve(RoomScanFile scan, MRUKRoom room,
                                          float maxError = 0.35f, float ambiguityMargin = 0.1f)
        {
            if (room == null) return RoomAlignment.None("no MRUK room to align to");

            if (scan?.room?.walls == null || scan.room.walls.Count < 2)
                return RoomAlignment.None($"the scan stores {scan?.room?.walls?.Count ?? 0} walls; " +
                                          $"at least 2 are needed to pin down a heading");

            var saved = ReadSaved(scan.room.walls);
            var current = ReadCurrent(room);

            if (saved.Count < 2) return RoomAlignment.None("the scan's walls have no usable size");
            if (current.Count < 2) return RoomAlignment.None($"MRUK reports {current.Count} walls");

            // The widest wall is the anchor for the search. Not because it is special, but
            // because it is the one most likely to survive a re-scan intact -- Space Setup
            // splits and merges small wall segments far more readily than large ones.
            var key = Widest(saved);

            // Y comes from the floor rather than the fit. The walls are vertical, so sliding
            // them up and down costs the score nothing and the fit would leave it unconstrained.
            var deltaY = CurrentFloorY(room) - scan.room.floorY;

            var bestError = float.PositiveInfinity;
            var runnerUp = float.PositiveInfinity;
            var bestYaw = 0f;
            var bestTranslation = Vector3.zero;

            // Every current wall is tried as the key wall's partner. No pre-filter on width:
            // a re-scan routinely measures the same wall a little longer or shorter, and a
            // width gate that looks reasonable is exactly how the correct answer gets thrown
            // out before it is ever scored.
            foreach (var candidate in current)
            {
                var yaw = Vector3.SignedAngle(key.Normal, candidate.Normal, Vector3.up);
                var rotation = Quaternion.AngleAxis(yaw, Vector3.up);

                var translation = SolveTranslation(saved, current, rotation, deltaY);
                var error = Score(saved, current, rotation, translation);

                // Ties break toward the smaller turn. When two orientations fit equally well
                // the room has probably barely moved, so the near-identity answer is the safer
                // of the two -- and if it is wrong, it is wrong by less.
                var better = error < bestError - 1e-4f
                             || (Mathf.Abs(error - bestError) <= 1e-4f
                                 && Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) < Mathf.Abs(Mathf.DeltaAngle(bestYaw, 0f)));

                if (better)
                {
                    runnerUp = bestError;
                    bestError = error;
                    bestYaw = yaw;
                    bestTranslation = translation;
                }
                else if (error < runnerUp)
                {
                    runnerUp = error;
                }
            }

            if (bestError > maxError)
                return RoomAlignment.None($"best wall fit was {bestError:F2} m, over the " +
                                          $"{maxError:F2} m limit -- keeping the file's own frame");

            var margin = float.IsPositiveInfinity(runnerUp) ? float.PositiveInfinity : runnerUp - bestError;
            var ambiguous = margin < ambiguityMargin;

            var summary = $"yaw {bestYaw:F1}deg, offset {bestTranslation.magnitude:F2} m, " +
                          $"error {bestError:F2} m over {saved.Count} walls";

            return new RoomAlignment(true, bestYaw, bestTranslation, bestError, margin, ambiguous, summary);
        }

        /// <summary>
        /// Places the rotated walls over the current ones. Starts from the centroids, which is
        /// stable but drags when the two rooms report different numbers of walls, then takes one
        /// refinement pass over the matched pairs to shake that out.
        /// </summary>
        private static Vector3 SolveTranslation(List<Wall> saved, List<Wall> current,
                                                Quaternion rotation, float deltaY)
        {
            var savedCentroid = Centroid(saved);
            var currentCentroid = Centroid(current);
            var translation = currentCentroid - rotation * savedCentroid;

            translation.y = deltaY;

            var sum = Vector3.zero;
            var matched = 0;

            foreach (var wall in saved)
            {
                var moved = rotation * wall.Center + translation;
                var normal = rotation * wall.Normal;

                if (!TryMatch(moved, normal, current, out var partner)) continue;

                sum += new Vector3(partner.Center.x - moved.x, 0f, partner.Center.z - moved.z);
                matched++;
            }

            if (matched > 0) translation += sum / matched;

            translation.y = deltaY;
            return translation;
        }

        /// <summary>Mean distance from each saved wall to the current wall it matched.</summary>
        private static float Score(List<Wall> saved, List<Wall> current,
                                   Quaternion rotation, Vector3 translation)
        {
            var total = 0f;

            foreach (var wall in saved)
            {
                var moved = rotation * wall.Center + translation;
                var normal = rotation * wall.Normal;

                total += TryMatch(moved, normal, current, out var partner)
                    ? Flat(partner.Center - moved).magnitude
                    : UnmatchedPenalty;
            }

            return total / saved.Count;
        }

        /// <summary>
        /// Nearest current wall that also faces roughly the same way. The facing test matters:
        /// in a narrow room the wall behind you can be closer to a mis-rotated guess than the
        /// wall it actually came from, and without it the score rewards that.
        /// </summary>
        private static bool TryMatch(Vector3 center, Vector3 normal, List<Wall> current, out Wall best)
        {
            best = default;
            var bestDistance = float.PositiveInfinity;
            var found = false;

            foreach (var candidate in current)
            {
                if (Vector3.Angle(normal, candidate.Normal) > NormalToleranceDegrees) continue;

                var distance = Flat(candidate.Center - center).magnitude;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = candidate;
                found = true;
            }

            return found;
        }

        private static List<Wall> ReadSaved(List<WallRecord> records)
        {
            var walls = new List<Wall>(records.Count);

            foreach (var record in records)
            {
                if (record?.center == null || record.rotation == null) continue;

                var normal = Flat(record.rotation.ToQuaternion() * Vector3.forward);
                if (normal.sqrMagnitude < 1e-6f) continue;   // a floor or ceiling slipped in

                walls.Add(new Wall(record.center.ToVector3(), normal.normalized,
                                   record.size?.x ?? 0f));
            }

            return walls;
        }

        /// <summary>
        /// Reads today's walls into the same room-local frame the recorder wrote, so the two
        /// sets are directly comparable.
        ///
        /// Note this takes the wall normal as local +Z on both sides without asserting that
        /// MRUK means forward by it. It does not matter: the yaw comes from the angle between
        /// two vectors produced the same way, so a wrong guess about which axis is the normal
        /// cancels out. It would only matter if the two sides disagreed.
        /// </summary>
        private static List<Wall> ReadCurrent(MRUKRoom room)
        {
            var walls = new List<Wall>(room.WallAnchors.Count);
            var inverse = Quaternion.Inverse(room.transform.rotation);

            foreach (var anchor in room.WallAnchors)
            {
                if (anchor == null) continue;

                var normal = Flat(inverse * anchor.transform.rotation * Vector3.forward);
                if (normal.sqrMagnitude < 1e-6f) continue;

                walls.Add(new Wall(room.transform.InverseTransformPoint(anchor.transform.position),
                                   normal.normalized,
                                   anchor.PlaneRect?.size.x ?? 0f));
            }

            return walls;
        }

        private static float CurrentFloorY(MRUKRoom room)
            => room.FloorAnchors.Count > 0
                ? room.transform.InverseTransformPoint(room.FloorAnchors[0].transform.position).y
                : 0f;

        private static Wall Widest(List<Wall> walls)
        {
            var best = walls[0];
            foreach (var wall in walls) if (wall.Width > best.Width) best = wall;
            return best;
        }

        private static Vector3 Centroid(List<Wall> walls)
        {
            var sum = Vector3.zero;
            foreach (var wall in walls) sum += wall.Center;
            return sum / walls.Count;
        }

        /// <summary>Drops Y. Every comparison here is a floor-plan one.</summary>
        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
