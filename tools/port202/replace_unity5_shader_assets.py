#!/usr/bin/env python3
"""Replace Unity 5 CGProgram placeholders with compilable Unity 6 shader sources.

AssetRipper exports some legacy shader objects as class ID 109 (CGProgram), a
serialized format Unity 6 cannot resolve safely. This keeps each Resources path
and asset GUID stable while producing an unlit, textured fallback shader.
Original YAML and metadata are copied to a recovery directory before conversion.
"""

from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path


SHADER_TEMPLATE = '''Shader "Transformers/Legacy/{name}"
{{
    Properties
    {{
        _MainTex ("Texture", 2D) = "white" {{}}
        _Color ("Color", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ "RenderType"="Opaque" "Queue"="Geometry" }}
        Pass
        {{
            Cull Back
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            struct appdata {{ float4 vertex : POSITION; float2 uv : TEXCOORD0; }};
            struct v2f {{ float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; }};
            v2f vert(appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }}
            fixed4 frag(v2f i) : SV_Target {{ return tex2D(_MainTex, i.uv) * _Color; }}
            ENDCG
        }}
    }}
    Fallback Off
}}
'''


def convert(project_assets: Path, backup_root: Path) -> int:
    converted = 0
    for asset in sorted(project_assets.rglob("*.asset")):
        try:
            source = asset.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if not re.search(r"(?m)^--- !u!109 &", source) or "CGProgram:" not in source:
            continue
        match = re.search(r"(?m)^  m_Name: (.*)$", source)
        name = match.group(1).strip() if match else asset.stem
        shader = asset.with_suffix(".shader")
        meta = asset.with_name(asset.name + ".meta")
        shader_meta = shader.with_name(shader.name + ".meta")
        if shader.exists() or not meta.is_file():
            raise RuntimeError(f"Cannot safely convert {asset}: destination or metadata missing")

        relative = asset.relative_to(project_assets)
        backup = backup_root / relative
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(asset, backup)
        shutil.copy2(meta, backup.with_name(backup.name + ".meta"))

        existing_meta = meta.read_text(encoding="utf-8", errors="replace")
        guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", existing_meta)
        if not guid:
            raise RuntimeError(f"No stable asset GUID in {meta}")
        shader_meta.write_text(
            "fileFormatVersion: 2\n"
            f"guid: {guid.group(1)}\n"
            "ShaderImporter:\n"
            "  externalObjects: {}\n"
            "  defaultTextures: []\n"
            "  nonModifiableTextures: []\n"
            "  preprocessorOverride: 0\n"
            "  userData:\n"
            "  assetBundleName:\n"
            "  assetBundleVariant:\n",
            encoding="utf-8",
        )
        shader.write_text(SHADER_TEMPLATE.format(name=name.replace('"', "")), encoding="utf-8")
        asset.unlink()
        meta.unlink()
        converted += 1
    for shader in project_assets.rglob("*.shader"):
        source = shader.read_text(encoding="utf-8", errors="replace")
        if 'Shader ""' not in source:
            continue
        shader_name = "Transformers/Imported/" + shader.relative_to(project_assets).with_suffix("").as_posix()
        shader_name = re.sub(r"[^A-Za-z0-9_./-]", "_", shader_name)
        shader.write_text(source.replace('Shader ""', f'Shader "{shader_name}"', 1), encoding="utf-8")
    return converted


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="Unity project Assets directory")
    parser.add_argument("backup", type=Path, help="directory for original shader YAML and metadata")
    args = parser.parse_args()
    print(f"Converted {convert(args.assets, args.backup)} legacy shader assets")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
