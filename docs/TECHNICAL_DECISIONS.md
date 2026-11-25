# Decisões técnicas

- **.NET 10 + WPF nativo, sem navegador.** WebRTC no navegador e Electron com `getDisplayMedia`/MediaRecorder costumam travar em 30–60 FPS e não dão controle do encoder. Manter o app nativo foi o caminho de menor custo a partir do MVP.
- **FFmpeg (build LGPL) em vez dos SDKs de cada fabricante.** Um único código cobre NVENC, AMF e Quick Sync, além dos decoders D3D11VA. As DLLs são carregadas dinamicamente (FFmpeg.AutoGen), sem modificação, o que mantém a LGPL simples de cumprir. O x264 é GPL, por isso a reserva por software da build distribuída é o OpenH264; uma build GPL do FFmpeg habilita o x264 sem mudar o código.
- **Windows.Graphics.Capture primeiro.** Inclui o cursor, captura janelas e monitores; DXGI Desktop Duplication fica de reserva para monitores.
- **Conversão de cor no Video Processor do D3D11.** BGRA→NV12 e escala na GPU, sem shaders próprios.
- **Zero-copy em NVENC e AMF.** A textura NV12 é copiada (GPU→GPU) para o pool de frames D3D11 do FFmpeg. QSV e software usam staging.
- **Baixa latência no encoder.** Sem B-frames, CBR com VBV de ~2 frames, intra-refresh no NVENC (período = 1 s), keyframe sob demanda.
- **UDP próprio em vez de reaproveitar Sunshine/Moonlight ou GStreamer.** Sunshine é GPL e é outro programa; GStreamer pesa mais de 100 MB e integra mal com WPF. O protocolo do ScreenFlow é pequeno (cabeçalho de 32 bytes, FEC XOR, NACK, AES-GCM) e testável.
- **Uma codificação para N viewers.** A GPU trabalha igual com 1 ou 20 pessoas; o upload é dividido entre elas.
- **Criptografia sempre ligada.** AES-GCM com AES-NI custa pouco perto do encoder e impede que outros na rede Radmin vejam a tela.
- **Radmin VPN como rede.** O host faz bind no IP 26.x; a descoberta usa broadcast na sub-rede do Radmin. Sem servidor de sinalização.
- **Métricas reais.** O overlay só mostra o que foi medido (FPS decodificado, bitrate recebido, perda pelos números dos pacotes, latência pela sincronia de relógio).
