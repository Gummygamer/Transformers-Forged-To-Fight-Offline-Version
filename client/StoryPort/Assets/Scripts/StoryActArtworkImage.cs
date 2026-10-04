using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace StoryPort
{
    // Crop UVs when the canvas builds its mesh, using the final panel dimensions.
    // This also works in immediate Editor captures without a layout/update frame.
    public sealed class StoryActArtworkImage : Image
    {
        public float focusX = .5f;

        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            if (sprite == null)
            {
                base.OnPopulateMesh(vertices);
                return;
            }

            vertices.Clear();
            var panel = GetPixelAdjustedRect();
            if (panel.width <= 0f || panel.height <= 0f) return;
            var uv = DataUtility.GetOuterUV(sprite);
            float sourceAspect = sprite.rect.width / sprite.rect.height;
            float panelAspect = panel.width / panel.height;
            float visibleWidth = Mathf.Min(1f, panelAspect / sourceAspect);
            float visibleHeight = Mathf.Min(1f, sourceAspect / panelAspect);
            float left = (1f - visibleWidth) * Mathf.Clamp01(focusX);
            float bottom = (1f - visibleHeight) * .5f;
            float u0 = Mathf.Lerp(uv.x, uv.z, left);
            float u1 = Mathf.Lerp(uv.x, uv.z, left + visibleWidth);
            float v0 = Mathf.Lerp(uv.y, uv.w, bottom);
            float v1 = Mathf.Lerp(uv.y, uv.w, bottom + visibleHeight);
            Color32 tint = color;
            vertices.AddVert(new Vector3(panel.xMin, panel.yMin), tint, new Vector2(u0, v0));
            vertices.AddVert(new Vector3(panel.xMin, panel.yMax), tint, new Vector2(u0, v1));
            vertices.AddVert(new Vector3(panel.xMax, panel.yMax), tint, new Vector2(u1, v1));
            vertices.AddVert(new Vector3(panel.xMax, panel.yMin), tint, new Vector2(u1, v0));
            vertices.AddTriangle(0, 1, 2);
            vertices.AddTriangle(2, 3, 0);
        }
    }
}
