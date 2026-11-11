# Security policy

## Reporting a vulnerability

Please **do not** report security issues in public issues, discussions or pull requests.

Report them privately through GitHub:
[Security → Report a vulnerability](https://github.com/QuenCode/Orbivo/security/advisories/new).
Describe the problem, how to reproduce it (for example a crafted video file) and which version you used.

Orbivo is maintained in spare time. You will usually get an answer within two weeks. Once a fix is released, the
advisory is published, with credit to you if you want.

## Supported versions

Only the latest release receives security fixes.

## Scope

Orbivo has no network functions: it opens local video, image and settings files and writes logs and recordings.
The most likely attack surface is therefore a **crafted media file** that reaches the FFmpeg demuxers and decoders.
Orbivo ships its own FFmpeg build ([build/ffmpeg](build/ffmpeg/README.md)) and updates it when FFmpeg publishes
security fixes. Problems inside FFmpeg, Steam Audio or the OpenXR loader should also be reported to those projects;
please tell us as well, so we can update.
