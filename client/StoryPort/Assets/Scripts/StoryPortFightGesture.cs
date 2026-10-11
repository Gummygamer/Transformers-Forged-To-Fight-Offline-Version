using UnityEngine;

namespace StoryPort
{
    // Classifies one fight touch. The 9.2 controls resolve a tap on release,
    // but a heavy attack fires while the right side is still held.
    public sealed class StoryPortFightGesture
    {
        public enum Action { None, Block, ReleaseBlock, GuardForward, GuardBack, Light, Heavy, RightSwipe, Advance, WalkForward, WalkBackward, LeftSwipe, SidestepLeft, SidestepRight }

        const float HoldSeconds = .2f;
        const float SwipeDistance = .09f;
        const float AdvanceDistance = .2f;
        Vector2 start;
        float startedAt;
        bool heavyTriggered;
        bool movementStarted;

        public bool Tracking { get; private set; }
        public bool Guarding { get; private set; }

        public Action Begin(Vector2 point, float time)
        {
            Cancel();
            // The attack hex occupies the bottom-right HUD strip (.035-.175 in
            // normalized screen coordinates), so taps there must use the same
            // gesture mapping as the central attack area.
            if (point.y < .035f || point.y > .72f) return Action.None;
            Tracking = true;
            start = point;
            startedAt = time;
            Guarding = point.x < .3f;
            return Guarding ? Action.Block : Action.None;
        }

        public Action Move(Vector2 point, float time)
        {
            if (!Tracking || Guarding || heavyTriggered || start.x <= .38f)
                return Action.None;
            var delta = point - start;
            if (Mathf.Abs(delta.x) >= AdvanceDistance)
            {
                movementStarted = true;
                return delta.x > 0f ? Action.WalkForward : Action.WalkBackward;
            }
            if (movementStarted || time - startedAt <= HoldSeconds)
                return Action.None;
            if (Mathf.Abs(delta.x) > SwipeDistance || Mathf.Abs(delta.y) > SwipeDistance)
                return Action.None;
            heavyTriggered = true;
            return Action.Heavy;
        }

        public Action End(Vector2 point, float time)
        {
            if (!Tracking) return Action.None;
            Tracking = false;
            var delta = point - start;
            if (Guarding)
            {
                Guarding = false;
                if (Mathf.Abs(delta.x) > SwipeDistance || Mathf.Abs(delta.y) > SwipeDistance)
                    return delta.x >= 0f ? Action.GuardForward : Action.GuardBack;
                return Action.ReleaseBlock;
            }
            if (heavyTriggered || movementStarted || start.x <= .38f) return Action.None;
            if (Mathf.Abs(delta.x) > SwipeDistance)
                return delta.x > 0f ? (delta.x >= AdvanceDistance ? Action.Advance : Action.RightSwipe) : Action.LeftSwipe;
            if (Mathf.Abs(delta.y) > SwipeDistance) return delta.y > 0f ? Action.SidestepLeft : Action.SidestepRight;
            return time - startedAt > HoldSeconds ? Action.Heavy : Action.Light;
        }

        public Action Cancel()
        {
            var release = Guarding ? Action.ReleaseBlock : Action.None;
            Tracking = false;
            Guarding = false;
            heavyTriggered = false;
            movementStarted = false;
            return release;
        }
    }
}
