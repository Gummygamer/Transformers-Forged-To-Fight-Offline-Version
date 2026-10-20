# Unity 2020.3.31f1 in Docker

`Dockerfile.ubuntu20` supplies Ubuntu 20.04's Unity runtime dependencies; the Unity Editor stays
on the host and is mounted read-only. `mono310/run.sh` drives the batch build with it.

```bash
docker build -t tftf-unity2020-ubuntu20 -f tools/unity/Dockerfile.ubuntu20 tools/unity
export UNITY_2020_ROOT=/path/to/Unity/Hub/Editor/2020.3.31f1
```

Unity binds `Unity_lic.ulf` to the host machine id, so the container needs `/etc/machine-id`,
the host name and the license file mounted (see `mono310/run.sh` for the exact invocation).
