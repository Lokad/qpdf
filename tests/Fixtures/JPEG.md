# Fixed JPEG controls

`cmyk-8x8.jpg` is an 8x8 four-component CMYK JPEG encoded once with
ImageSharp 3.1.12 at quality 75. Source RGB samples were `(32*x, 32*y, 128)`.
It replaces repeated fixture encoding after the development provider migration;
it tests four-component opacity and pass-through, not a CMYK decoder claim.

The committed progressive PDF fixtures remain independent decoding controls.
Their exact constant samples and the existing malformed/parameterized fixtures
must remain valid regardless of which development JPEG provider is selected.
