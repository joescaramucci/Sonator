# Third-party notices

Sonator is licensed under the GNU General Public License v3.0 (see `LICENSE.txt`).
It includes or uses the following third-party components.

## DryWetMIDI

- Package: `Melanchall.DryWetMidi` 8.0.3 (NuGet), used for MIDI file reading and writing.
- License: MIT. Copyright (c) Melanchall.

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
