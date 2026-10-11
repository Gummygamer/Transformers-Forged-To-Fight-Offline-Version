# Unity 2020.3.31f1 in Docker

The recovery image supplies Ubuntu 20.04's Unity runtime dependencies. The
Unity Editor and its bundled Mono runtime stay on the external volume and are
mounted read-only, avoiding an additional 11 GB copy in Docker storage.

```bash
export UNITY_2020_ROOT=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/Unity/Hub/Editor/2020.3.31f1
export TFTF_UNITY_PROJECT=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/Transformers-Forged-To-Fight-Offline-Version/build/analysis/rea/9.2/recovery-9.2-confirmed-source-v1/unity-rebuild
docker compose -f tools/unity/compose.ubuntu20.yaml run --rm unity2020-31f1
docker compose -f tools/unity/compose.ubuntu20.yaml run --rm --entrypoint /bin/bash unity2020-31f1 -lc '/opt/unity/Editor/Data/MonoBleedingEdge/bin-linux64/mono --version; /opt/unity/Editor/Data/MonoBleedingEdge/bin-linux64/mcs --version'
```

The first command should print Unity `2020.3.31f1`; the second reports the
Mono runtime and C# compiler shipped with that same Editor. Set both variables
to the current external mount paths before running Compose. The regular guarded
rebuild remains `tools/build_recovered_9_2.py`; this Compose service provides a
reproducible container environment without relocating or duplicating the
installation.
