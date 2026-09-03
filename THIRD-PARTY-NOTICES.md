# Third-party notices

SimpleVoiceChat is owned and maintained by VSCN-Studio. `HansJack` is the founder of the VSCN-Studio team. Project source is distributed under the license in `LICENSE`.

SimpleVoiceChat includes Concentus 2.2.2 for managed Opus audio encoding and decoding. The complete license text is included in `assets/simplevoicechat/licenses/CONCENTUS-LICENSE.txt`.

Concentus is distributed under the BSD 3-Clause License. Its source and license are available at:

- https://github.com/lostromb/concentus
- https://www.nuget.org/packages/Concentus/2.2.2

Copyright (c) 2013-2025, Eric Lasota and Concentus contributors.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the conditions in the upstream `LICENSE` file are met. The complete upstream license is included in the Concentus NuGet package referenced by this project.

The settings controls include Lucide icons from https://lucide.dev/ under the ISC License.
The complete license text is included at `assets/simplevoicechat/licenses/LUCIDE.txt`.

SimpleVoiceChat bundles the third-party `YellowDogMan.RRNoise.NET` 0.1.9 package for local microphone noise suppression. Its native builds are included for Windows x64/x86 and Linux x64/arm64. The package is maintained and built by Yellow Dog Man Studios S.r.o.; SimpleVoiceChat does not claim authorship of these binaries.

- https://github.com/Yellow-Dog-Man/RNNoise.Net
- https://www.nuget.org/packages/YellowDogMan.RRNoise.NET/0.1.9

The `YellowDogMan.RRNoise.NET` managed wrapper is distributed under the MIT License. The complete license text is included in `assets/simplevoicechat/licenses/RRNOISE.NET-LICENSE.txt`.

- https://licenses.nuget.org/MIT

The bundled native RNNoise library is distributed under the BSD 3-Clause License. The exact upstream text, including all copyright holders and disclaimer, is included in `assets/simplevoicechat/licenses/RNNOISE-LICENSE.txt`.

- https://gitlab.xiph.org/xiph/rnnoise

The optional local speech-recognition provider uses Whisper.net 1.9.1 under the MIT License. The complete license text is included in `assets/simplevoicechat/licenses/WHISPER.NET-LICENSE.txt`. The matching managed and native runtime files are supplied by the separate `SimpleVoiceChatASR` client dependency package.

- https://github.com/sandrohanea/whisper.net
- https://www.nuget.org/packages/Whisper.net/1.9.1
- https://www.nuget.org/packages/Whisper.net.Runtime/1.9.1

Whisper.net depends on Microsoft.Extensions.AI.Abstractions 10.2.0, distributed under the MIT License. The complete license text is included in `assets/simplevoicechat/licenses/MICROSOFT-EXTENSIONS-AI-LICENSE.txt`.

Whisper.net.Runtime native binaries include whisper.cpp/ggml code under the MIT License. The complete upstream text is included in `assets/simplevoicechat/licenses/WHISPER-CPP-LICENSE.txt`.
