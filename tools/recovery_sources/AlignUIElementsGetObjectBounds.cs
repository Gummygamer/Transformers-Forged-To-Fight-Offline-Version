using EB;
using UnityEngine;

namespace RecoverySources
{
    // Authored from the 9.2 ARM64 trace for AlignUIElements.GetObjectBounds.
    // The compiled method body is transplanted into the recovered game assembly;
    // this helper type itself is not included in the rebuilt player.
    public static class AlignUIElementsGetObjectBounds
    {
        public static void Replace(AlignUIElements self,
            AlignUIElements.AlignedObject obj,
            out Vector3 localMin,
            out Vector3 localMax)
        {
            Bounds bounds;
            EBGWidgetContainer holder = obj.widgetHolder;
            if (holder != null)
            {
                bounds = holder.bounds;
                if (bounds.extents == Vector3.zero)
                {
                    holder.boundsDirty = true;
                    bounds = holder.bounds;
                }
            }
            else
            {
                UIWidget widget = obj.widget;
                Vector3[] corners;
                if (widget is UILabel && !obj.labelUsesWidgetCorners)
                {
                    corners = UIUtils.GetLabelWorldCorners((UILabel)widget);
                }
                else
                {
                    corners = widget.worldCorners;
                }

                bounds = new Bounds(corners[0], Vector3.zero);
                for (int i = 1; i < corners.Length; i++)
                {
                    bounds.Encapsulate(corners[i]);
                }
            }

            Matrix4x4 worldToLocal = self.transform.worldToLocalMatrix;
            localMin = worldToLocal.MultiplyPoint3x4(bounds.min);
            worldToLocal = self.transform.worldToLocalMatrix;
            localMax = worldToLocal.MultiplyPoint3x4(bounds.max);

            float absolute = Mathf.Abs(localMin.x);
            float fraction = absolute - Mathf.Floor(absolute);
            localMin.x = fraction > 0.1f ? Mathf.Floor(localMin.x) : Mathf.Ceil(localMin.x);

            absolute = Mathf.Abs(localMin.y);
            fraction = absolute - Mathf.Floor(absolute);
            localMin.y = fraction > 0.1f ? Mathf.Floor(localMin.y) : Mathf.Ceil(localMin.y);

            absolute = Mathf.Abs(localMax.x);
            fraction = absolute - Mathf.Floor(absolute);
            localMax.x = fraction > 0.1f ? Mathf.Floor(localMax.x) : Mathf.Ceil(localMax.x);

            absolute = Mathf.Abs(localMax.y);
            fraction = absolute - Mathf.Floor(absolute);
            localMax.y = fraction > 0.1f ? Mathf.Floor(localMax.y) : Mathf.Ceil(localMax.y);
        }
    }
}
