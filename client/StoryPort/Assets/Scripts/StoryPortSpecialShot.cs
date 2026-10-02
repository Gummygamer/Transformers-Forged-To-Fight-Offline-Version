using UnityEngine;

namespace StoryPort
{
    // One clock drives the hit, camera shot and combat lock. The reference's
    // mid-length special lands at about 0.7 s and releases its shot near 1.85 s.
    public static class StoryPortSpecialTimeline
    {
        public const float ImpactSeconds = .7f;
        public const float RecoverySeconds = 1.85f;

        // The reference holds "K.O." over the stricken bot for about 1.5 s with the
        // HUD still up, then cuts to a brief front shot of the winner.
        public const float KnockoutHoldSeconds = 1.5f;
        public const float WinnerCloseUpSeconds = .45f;

        // Converted special clips range from roughly 2 to 7 seconds. Speed a
        // longer clip up to fit recovery; shorter clips play naturally and are
        // explicitly returned to idle when the shared timeline ends.
        public static float PlaybackSpeed(float clipLength, float baselineSpeed = 1f)
        {
            return Mathf.Max(baselineSpeed, clipLength / RecoverySeconds);
        }

        // A buffered attack must wait for both the current action and enemy turn.
        public static bool CanRunQueuedAttack(bool queued, bool enemyBusy, bool playerBusy, bool inFight, bool enemyAlive)
        {
            return queued && !enemyBusy && !playerBusy && inFight && enemyAlive;
        }
    }

    // Lifecycle and framing for the special-attack cinematic camera. The shot
    // borrows the fight camera, so exactly one owner may hold it at a time and
    // whoever ends it restores the saved fight pose once. Keeping this separate
    // from the bootstrap lets Editor checks cover the transitions.
    public sealed class StoryPortSpecialShot
    {
        public struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;
        }

        int owner;
        Pose home;

        public bool Active { get; private set; }
        public Pose Home { get { return home; } }

        // Returns the shot's id, or 0 when another shot already owns the camera.
        public int Begin(Pose fightPose)
        {
            if (Active) return 0;
            Active = true;
            home = fightPose;
            return ++owner;
        }

        public bool Owns(int id)
        {
            return Active && id == owner;
        }

        // Ends the shot (normal finish, knockout, interruption, screen change).
        // True when a pose is returned that the caller must restore; false when
        // the shot had already ended, so the camera is never restored twice.
        public bool End(out Pose fightPose)
        {
            fightPose = home;
            if (!Active) return false;
            Active = false;
            return true;
        }

        // Cross-fade weight: ease in at the start and out at the end.
        public static float Blend(float elapsed, float total)
        {
            float k = Mathf.Clamp01(elapsed / Mathf.Max(.01f, total));
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / .18f)) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((k - .82f) / .18f)));
        }

        // Scene dim weight. The reference darkens the world (not the HUD) almost
        // at once, holds it through the hits and lifts it near the shot's end.
        public static float Dim(float elapsed, float total)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / .15f)) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((elapsed - (total - .3f)) / .3f)));
        }

        // The reference special is a wide, low, side-on shot with both fighters
        // in frame, not a close-up. Distance is chosen from the lens and aspect so
        // the pair (plus a margin for their bulk) always fits the view width.
        public const float FrameMargin = 6f;
        public const float MinDistance = 7f;
        public const float CameraHeight = 1.8f;
        const float FocusHeight = 2.2f;

        public static float FitDistance(float separation, float verticalFov, float aspect)
        {
            float halfWidth = Mathf.Tan(Mathf.Clamp(verticalFov, 5f, 120f) * .5f * Mathf.Deg2Rad) * Mathf.Max(.5f, aspect);
            return Mathf.Max(MinDistance, (separation + FrameMargin) * .5f / halfWidth);
        }

        // Camera position and focus for the shot. `sideHint` points toward the
        // fight camera's side so the shot never crosses to the far side of the pair.
        // `track` (0..1) drifts the focus a little toward the target over the shot.
        public static void Frame(Vector3 attacker, Vector3 target, Vector3 sideHint, float verticalFov, float aspect, float track,
            out Vector3 position, out Vector3 focus)
        {
            var axis = target - attacker; axis.y = 0f;
            float separation = axis.magnitude;
            axis = separation > .01f ? axis / separation : Vector3.right;
            var side = Vector3.Cross(Vector3.up, axis);
            if (Vector3.Dot(side, sideHint) < 0f) side = -side;
            var mid = (attacker + target) * .5f;
            float distance = FitDistance(separation, verticalFov, aspect);
            position = mid + side * distance + Vector3.up * CameraHeight;
            focus = Vector3.Lerp(mid, target, .22f * Mathf.Clamp01(track)) + Vector3.up * FocusHeight;
        }
    }
}
