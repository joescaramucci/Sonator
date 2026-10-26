# Third-party notices

Sonator is licensed under the GNU General Public License v3.0 (see `LICENSE.txt`).
It includes or uses the following third-party components.

## DryWetMIDI

- Package: `Melanchall.DryWetMidi` 8.0.3 (NuGet), used for MIDI file reading and writing.
- License: MIT. Copyright (c) Melanchall.

## TagLibSharp

- Package: `TagLibSharp` 2.3.0 (NuGet), used for reading and writing the tags of song files (MP3, WAV...).
- License: LGPL 2.1. Copyright (c) the TagLib# authors. It is linked as a separate, replaceable library.

## NAudio.Vorbis and NVorbis

- Packages: `NAudio.Vorbis` 1.5.0 and its dependency `NVorbis` 0.10.4 (NuGet), used for playing Ogg Vorbis files: OGG samples and the preview recordings of Kontakt instruments.
- License: MIT. Copyright (c) Andrew Ward.

## ONNX Runtime

- Package: `Microsoft.ML.OnnxRuntime` 1.31.0 (NuGet), used for running the stem separation model.
- License: MIT. Copyright (c) Microsoft Corporation.

## HT-Demucs (downloaded, not included)

- The stem separation model, HT-Demucs v4 by Meta Platforms (FAIR), as an ONNX export (`htdemucs_fp16weights.onnx`, huggingface.co/adowu/htdemucs-onnx), is downloaded on first use, after the player agrees, and checked against its SHA-256. It is not part of this repository.
- The Demucs code is MIT-licensed. The export is published under MIT; the original authors have described the pretrained weights as provided for research, so the stems are meant for the player's own music.

## VST 3 interfaces (pluginterfaces)

- Location: `native/VstHostBridge/thirdparty/pluginterfaces/`.
- Copyright (c) Steinberg Media Technologies GmbH.
- License: MIT. The full text is in `native/VstHostBridge/thirdparty/pluginterfaces/LICENSE.txt`.

## ASIO SDK (optional)

- Used only when the bridge is built with `SONATOR_ASIO_SDK_DIR` set. The SDK is not included in this repository.
- Copyright (c) Steinberg Media Technologies GmbH.
- The SDK is offered under two licenses: the Steinberg proprietary ASIO SDK license, or the GNU GPL v3. Sonator uses the GPL v3 option.
- Only the SDK's header files (`common/asio.h` and `common/iasiodrv.h`) are compiled into the bridge.

## Trademarks

"VST" is a trademark of Steinberg Media Technologies GmbH. "ASIO" is a trademark and technology of Steinberg Media Technologies GmbH. Sonator is not affiliated with or endorsed by Steinberg.
