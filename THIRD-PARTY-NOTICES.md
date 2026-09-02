# Third-party notices

SimpleVoiceChat includes Concentus 2.2.2 for managed Opus audio encoding and decoding.

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

The `YellowDogMan.RRNoise.NET` managed wrapper is distributed under the MIT License. The complete license text is available at:

- https://licenses.nuget.org/MIT

The bundled native RNNoise library is distributed under the BSD 3-Clause License.

- https://gitlab.xiph.org/xiph/rnnoise

Copyright (c) 2003-2024, the RNNoise, Xiph.Org Foundation, Mozilla, Amazon, and other upstream contributors.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the Xiph.Org Foundation nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE FOUNDATION OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The optional local speech-recognition provider uses Whisper.net 1.9.1 under the MIT License. The matching managed and native runtime files are supplied by the separate `SimpleVoiceChatASR` client dependency package.

- https://github.com/sandrohanea/whisper.net
- https://www.nuget.org/packages/Whisper.net/1.9.1
- https://www.nuget.org/packages/Whisper.net.Runtime/1.9.1
