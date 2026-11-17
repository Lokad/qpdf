namespace Lokad.Qpdf.Json;

// The same structural description serves public help and output checking.
// Descriptive strings leave PDF values unconstrained; angle-bracket keys
// describe maps, singleton arrays describe sequences or one matching value.
internal static class JsonFormat
{
    private const string Common = """
        "parameters": {"decodelevel": "decode level used for image filterability"},
        "pages": [{
          "object": "page object reference or direct value",
          "images": [{
            "bitspercomponent": "stored image sample width",
            "colorspace": "stored color space",
            "decodeparms": ["stored filter parameters"],
            "filter": ["stored filter name"],
            "filterable": "supported parameters and requested decode level",
            "height": "stored image height", "width": "stored image width",
            "name": "resource name", "object": "image object reference"
          }],
          "contents": ["content stream reference or stored value"],
          "label": "effective label with its per-page start",
          "outlines": [{"object": "outline reference or value", "title": "outline title", "dest": "resolved explicit or named destination"}],
          "pageposfrom1": "one-based page position"
        }],
        "pagelabels": [{"index": "zero-based definition position", "label": "page-label definition with default start"}],
        "outlines": [{
          "object": "outline reference or value", "title": "outline title",
          "dest": "resolved explicit or named destination", "open": "initial open state",
          "destpageposfrom1": "one-based target page or null", "kids": "child outlines with the same structure"
        }],
        "acroform": {
          "hasacroform": "whether the document declares a form",
          "needappearances": "declared appearance requirement",
          "fields": [{
            "object": "field reference or value", "parent": "parent reference or null",
            "pageposfrom1": "one-based widget page", "fieldtype": "inherited field type",
            "fieldflags": "inherited field flags", "ischeckbox": "checkbox field",
            "isradiobutton": "radio field", "ischoice": "choice field", "istext": "text field",
            "fullname": "qualified field name", "partialname": "local field name",
            "alternativename": "local alternative name with fallback", "mappingname": "local mapping name with fallback",
            "value": "inherited value", "defaultvalue": "inherited default value",
            "choices": "inherited choice labels", "quadding": "effective alignment",
            "annotation": {"object": "widget reference or value", "annotationflags": "widget flags", "appearancestate": "widget appearance state"}
          }]
        },
        "attachments": {"<attachment-key>": {
          "filespec": "file specification reference or null", "preferredname": "preferred filename",
          "preferredcontents": "preferred stream reference or null", "description": "description or null",
          "names": {"<name-key>": "filename"},
          "streams": {"<stream-key>": {
            "checksum": "stored checksum or null", "creationdate": "creation timestamp or null",
            "modificationdate": "modification timestamp or null", "mimetype": "MIME type or null"
          }}
        }},
        "encrypt": {
          "encrypted": "document protection", "userpasswordmatched": "user credential matched",
          "ownerpasswordmatched": "owner credential matched", "recovereduserpassword": "null; secret is not disclosed",
          "parameters": {
            "R": "security revision", "V": "algorithm version", "P": "permission word", "bits": "key width",
            "key": "null; key is not disclosed", "method": "overall cipher", "streammethod": "stream cipher",
            "stringmethod": "string cipher", "filemethod": "embedded-file cipher"
          },
          "capabilities": {
            "accessibility": "accessibility extraction", "extract": "content extraction", "printlow": "low-resolution printing",
            "printhigh": "high-resolution printing", "modifyassembly": "document assembly", "modifyforms": "form changes",
        """;

    private const string EncryptionEnd = """
            "modifyother": "other changes", "modify": "all changes"
          }
        },
        """;

    internal const string VersionOne = """
        {"version": "JSON format version 1",
        """ + Common + "\"moddifyannotations\": \"annotation changes\"," + EncryptionEnd + """
          "objects": {"<n g R|trailer>": "PDF object value or stream dictionary"},
          "objectinfo": {"<object-reference>": {"stream": {
            "is": "whether this is a stream", "length": "stored length or null", "filter": "stored filter or null"
          }}}
        }
        """;

    internal const string VersionTwo = """
        {"version": "JSON format version 2",
        """ + Common + "\"modifyannotations\": \"annotation changes\"," + EncryptionEnd + """
          "qpdf": [
            {"jsonversion": "object format version", "pdfversion": "source PDF version",
             "pushedinheritedpageresources": "whether inherited resources were pushed to pages",
             "calledgetallpages": "whether pages were traversed", "maxobjectid": "largest object number"},
            {"<obj:n g R|trailer>": "PDF value or stream envelope"}
          ]
        }
        """;
}
