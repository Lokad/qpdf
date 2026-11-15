# Fixed JPEG controls

`cmyk-8x8.jpg` is an 8x8 four-component CMYK JPEG encoded once with
ImageSharp 3.1.12 at quality 75. Source RGB samples were `(32*x, 32*y, 128)`.
Tests use it for four-component opacity/pass-through and for raw CMYK decoding
through the explicit caller-codec capability. Four-component encoding remains unavailable.

The committed progressive PDF fixtures remain independent decoding controls.
Their exact constant samples and the existing malformed/parameterized fixtures
must remain valid regardless of which development JPEG provider is selected.
