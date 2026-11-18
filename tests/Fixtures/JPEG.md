# Fixed JPEG controls

`cmyk-8x8.jpg` is an 8x8 four-component CMYK JPEG encoded once with
ImageSharp 3.1.12 at quality 75. Source RGB samples were `(32*x, 32*y, 128)`.
Tests use it for four-component opacity/pass-through and for raw CMYK decoding
through the explicit caller-codec capability. Four-component encoding remains unavailable.

The four progressive PDF fixtures have constant samples and independent DC/AC
scans. The manifest records lengths, hashes and decoded sample digests.
[Progressive fixture generator](../../tools/build-progressive-fixtures.py) ·
[Fixture manifest](manifest.json)

The `rgb` visual case checks each quality-75 JPEG output against the original
image. A cross-encoder difference receives a separate result. The missing-image
control must fail. [Run the visual checks](../../docs/COMPATIBILITY.md#how-compatibility-is-verified) ·
[Recorded Lokad.Image 0.2.0 comparison](https://github.com/Lokad/qpdf/blob/5afca3f2bb21e4b9a0f6c16e8a3c2d9a6ec596d7/tests/Fixtures/JPEG.md)
