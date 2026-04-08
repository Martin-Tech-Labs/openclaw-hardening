# openclaw-hardening

## FusePidProbe

This repo now includes a small .NET console app under `src/FusePidProbe` that mounts a test FUSE filesystem and logs the caller context for each access.

### What it prints on each access
- **PID**: process id of the caller
- **UID**: Unix user id of the caller
- **GID**: Unix group id of the caller
- executable path
- code signature summary (best effort)
- parent process tree

### Run

```bash
cd src/FusePidProbe
dotnet run -- /tmp/fuse-pid-probe
```

Then in another shell:

```bash
ls -la /tmp/fuse-pid-probe
cat /tmp/fuse-pid-probe/probe.txt
```

### Notes
- `gid` means the Unix **group id** of the calling process.
- Signature reporting is best-effort using either `X509Certificate.CreateFromSignedFile` or `codesign -dv`.
- This is intended as a probe/demo app for process-aware gating experiments.
