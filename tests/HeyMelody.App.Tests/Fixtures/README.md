# Animated WebP regression inputs

`two-frame.webp` is a generated 32 × 32 lossless animation with 100 ms and 300 ms
frames. The second frame changes a small part of the first frame's red rectangle
to cyan. It exercises duration, blending, transparency, and infinite replay.

`x4-first-two-frames.webp` retains the VP8X, ANIM, and first two ANMF chunks from
the official Enco X4 `detail_model.webp`, byte for byte. Only the RIFF length was
updated. The original file's SHA256 is
`269c63cccf729ea819a38cc010f6c7f6ed86fefa00bc680e84f1e4863f3d4736`.
The two 33 ms frames use different partial rectangles and disposal/blending flags.
`x4-frame-000.png` and `x4-frame-001.png` are independent Pillow RGBA decodes used
as pixel references. These files are test inputs, not application artwork.

Source request and byte-level extraction evidence are retained locally under
`research/online-artwork/x4-artwork-audit/20261008T220302.957674Z/inspection/`.
