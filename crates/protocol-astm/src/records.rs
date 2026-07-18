//! ASTM E1394 record parsing into a **lossless** intermediate representation.
//!
//! Records are delimited text (fields by `|`, repeats by `\`, components by `^`,
//! escape `&`), with the delimiters self-described by the Header (`H`) record.
//! This layer only *structures* the bytes — it assigns **no clinical meaning**
//! (no unit/scale/status inference; that is a later, gated mapping step). Every
//! level keeps its raw bytes, and unknown record types and extra fields are
//! preserved, so nothing is lost. Malformed input never panics.

use thiserror::Error;

/// The delimiter set for a message. Defaults are the ASTM standard `| \ ^ &`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Delimiters {
    /// Field separator (default `|`).
    pub field: u8,
    /// Repeat separator (default `\`).
    pub repeat: u8,
    /// Component separator (default `^`).
    pub component: u8,
    /// Escape character (default `&`). Escape-sequence expansion is deferred to
    /// mapping; raw bytes are preserved here.
    pub escape: u8,
}

impl Default for Delimiters {
    fn default() -> Self {
        Self {
            field: b'|',
            repeat: b'\\',
            component: b'^',
            escape: b'&',
        }
    }
}

impl Delimiters {
    /// Derive the delimiters from a Header (`H`) record's raw bytes. The byte
    /// after `H` is the field delimiter; the following delimiter-definition field
    /// holds repeat, component, and escape characters in that order. Missing
    /// values fall back to the ASTM defaults.
    #[must_use]
    pub fn from_header(raw: &[u8]) -> Self {
        let default = Delimiters::default();
        let field = raw.get(1).copied().unwrap_or(default.field);
        let defn = raw
            .get(2..)
            .and_then(|rest| rest.split(|&b| b == field).next())
            .unwrap_or(&[]);
        Self {
            field,
            repeat: defn.first().copied().unwrap_or(default.repeat),
            component: defn.get(1).copied().unwrap_or(default.component),
            escape: defn.get(2).copied().unwrap_or(default.escape),
        }
    }
}

/// The type of an ASTM record, from its leading character. Unrecognized types are
/// preserved as [`RecordKind::Other`] rather than dropped.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecordKind {
    /// `H` — message header.
    Header,
    /// `P` — patient.
    Patient,
    /// `O` — test order.
    Order,
    /// `R` — result.
    Result,
    /// `C` — comment.
    Comment,
    /// `Q` — query/request.
    Query,
    /// `L` — message terminator.
    Terminator,
    /// `M` — manufacturer/vendor-specific.
    Manufacturer,
    /// `S` — scientific.
    Scientific,
    /// Any other/unknown leading character (preserved).
    Other(char),
}

impl RecordKind {
    fn from_first(byte: Option<u8>) -> Self {
        match byte {
            Some(b'H') => RecordKind::Header,
            Some(b'P') => RecordKind::Patient,
            Some(b'O') => RecordKind::Order,
            Some(b'R') => RecordKind::Result,
            Some(b'C') => RecordKind::Comment,
            Some(b'Q') => RecordKind::Query,
            Some(b'L') => RecordKind::Terminator,
            Some(b'M') => RecordKind::Manufacturer,
            Some(b'S') => RecordKind::Scientific,
            Some(other) => RecordKind::Other(other as char),
            None => RecordKind::Other('\0'),
        }
    }
}

/// One repeat within a field: an ordered list of components (byte strings), plus
/// the repeat's raw bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Repeat {
    /// Raw bytes of this repeat.
    pub raw: Vec<u8>,
    /// Component byte strings (may be empty).
    pub components: Vec<Vec<u8>>,
}

/// One field: an ordered list of repeats, plus the field's raw bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Field {
    /// Raw bytes of this field.
    pub raw: Vec<u8>,
    /// Repeats (at least one).
    pub repeats: Vec<Repeat>,
}

impl Field {
    /// The field's raw bytes as a lossy UTF-8 string (for display/tests).
    #[must_use]
    pub fn text(&self) -> String {
        String::from_utf8_lossy(&self.raw).into_owned()
    }

    /// The first component of the first repeat, if any, as lossy UTF-8.
    #[must_use]
    pub fn first_component_text(&self) -> Option<String> {
        self.repeats
            .first()
            .and_then(|r| r.components.first())
            .map(|c| String::from_utf8_lossy(c).into_owned())
    }
}

/// A single parsed record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Record {
    /// Record type.
    pub kind: RecordKind,
    /// Exact record bytes (without the terminating `CR`).
    pub raw: Vec<u8>,
    /// Fields; `fields[0]` is the record-type field.
    pub fields: Vec<Field>,
}

impl Record {
    /// Borrow a field by 0-based index (`fields[0]` is the type field).
    #[must_use]
    pub fn field(&self, index: usize) -> Option<&Field> {
        self.fields.get(index)
    }
}

/// A parsed ASTM message: its raw bytes, resolved delimiters, and records.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    /// The exact message bytes as received.
    pub raw: Vec<u8>,
    /// Delimiters resolved from the header (or defaults).
    pub delimiters: Delimiters,
    /// The records, in order.
    pub records: Vec<Record>,
}

/// Errors parsing a message. Parsing never panics; malformed input yields these.
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum RecordError {
    /// The message contained no records.
    #[error("empty message")]
    Empty,
    /// A record exceeded the configured maximum length.
    #[error("record exceeds maximum length ({0} bytes)")]
    RecordTooLong(usize),
}

fn split_on(bytes: &[u8], delim: u8) -> Vec<&[u8]> {
    bytes.split(move |&b| b == delim).collect()
}

fn parse_field(raw: &[u8], d: &Delimiters) -> Field {
    let repeats = split_on(raw, d.repeat)
        .into_iter()
        .map(|rep| Repeat {
            raw: rep.to_vec(),
            components: split_on(rep, d.component)
                .into_iter()
                .map(<[u8]>::to_vec)
                .collect(),
        })
        .collect();
    Field {
        raw: raw.to_vec(),
        repeats,
    }
}

/// Parse a single record line (no trailing `CR`) with the given delimiters.
#[must_use]
pub fn parse_record(line: &[u8], delimiters: &Delimiters) -> Record {
    let fields = split_on(line, delimiters.field)
        .into_iter()
        .map(|f| parse_field(f, delimiters))
        .collect();
    Record {
        kind: RecordKind::from_first(line.first().copied()),
        raw: line.to_vec(),
        fields,
    }
}

/// Parse a whole message. Records are separated by `CR`; `LF` is tolerated.
/// Delimiters come from the leading `H` record if present, else the defaults.
/// `max_record_len` bounds each record.
pub fn parse_message(bytes: &[u8], max_record_len: usize) -> Result<Message, RecordError> {
    let lines: Vec<&[u8]> = bytes
        .split(|&b| b == b'\r')
        .map(|line| {
            // Tolerate a stray LF at either end of a record.
            let line = line.strip_prefix(b"\n").unwrap_or(line);
            line.strip_suffix(b"\n").unwrap_or(line)
        })
        .filter(|line| !line.is_empty())
        .collect();

    if lines.is_empty() {
        return Err(RecordError::Empty);
    }

    let delimiters = if lines[0].first() == Some(&b'H') {
        Delimiters::from_header(lines[0])
    } else {
        Delimiters::default()
    };

    let mut records = Vec::with_capacity(lines.len());
    for line in lines {
        if line.len() > max_record_len {
            return Err(RecordError::RecordTooLong(line.len()));
        }
        records.push(parse_record(line, &delimiters));
    }

    Ok(Message {
        raw: bytes.to_vec(),
        delimiters,
        records,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &[u8] =
        b"H|\\^&|||analyzer^1|||||host||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";

    #[test]
    fn parses_records_and_kinds() {
        let msg = parse_message(SAMPLE, 4096).unwrap();
        let kinds: Vec<_> = msg.records.iter().map(|r| r.kind).collect();
        assert_eq!(
            kinds,
            vec![
                RecordKind::Header,
                RecordKind::Patient,
                RecordKind::Order,
                RecordKind::Result,
                RecordKind::Terminator,
            ]
        );
    }

    #[test]
    fn derives_standard_delimiters_from_header() {
        let msg = parse_message(SAMPLE, 4096).unwrap();
        assert_eq!(
            msg.delimiters,
            Delimiters {
                field: b'|',
                repeat: b'\\',
                component: b'^',
                escape: b'&',
            }
        );
    }

    #[test]
    fn derives_custom_delimiters_from_header() {
        // Field '#', repeat '~', component '*', escape '!'.
        let msg = parse_message(b"H#~*!#sender\rL#1\r", 4096).unwrap();
        assert_eq!(msg.delimiters.field, b'#');
        assert_eq!(msg.delimiters.repeat, b'~');
        assert_eq!(msg.delimiters.component, b'*');
        assert_eq!(msg.delimiters.escape, b'!');
    }

    #[test]
    fn splits_fields_components_and_repeats() {
        let msg = parse_message(SAMPLE, 4096).unwrap();
        let result = &msg.records[3];
        assert_eq!(result.kind, RecordKind::Result);
        // Field 2 of R is the universal test id "^^^GLU" → 4 components.
        let test = result.field(2).unwrap();
        assert_eq!(test.repeats[0].components.len(), 4);
        assert_eq!(test.repeats[0].components[3], b"GLU");
        // Value field is preserved exactly (no numeric interpretation here).
        assert_eq!(result.field(3).unwrap().text(), "5.30");
        // Reference-range field "3.9^5.6" → 2 components.
        assert_eq!(
            result.field(5).unwrap().repeats[0].components,
            vec![b"3.9".to_vec(), b"5.6".to_vec()]
        );
    }

    #[test]
    fn repeats_are_split() {
        // A field "a^b\c^d" has two repeats each with two components.
        let d = Delimiters::default();
        let rec = parse_record(b"R|1|a^b\\c^d", &d);
        let f = rec.field(2).unwrap();
        assert_eq!(f.repeats.len(), 2);
        assert_eq!(f.repeats[0].components, vec![b"a".to_vec(), b"b".to_vec()]);
        assert_eq!(f.repeats[1].components, vec![b"c".to_vec(), b"d".to_vec()]);
    }

    #[test]
    fn unknown_record_type_is_preserved() {
        let msg = parse_message(b"H|\\^&|\rZ|1|vendor-secret\rL|1\r", 4096).unwrap();
        assert_eq!(msg.records[1].kind, RecordKind::Other('Z'));
        // Raw bytes preserved for the unknown record.
        assert_eq!(msg.records[1].raw, b"Z|1|vendor-secret");
    }

    #[test]
    fn raw_is_lossless_per_record() {
        let msg = parse_message(SAMPLE, 4096).unwrap();
        // Reconstruct by joining record raws with CR (+ trailing CR) == input.
        let mut rebuilt = Vec::new();
        for r in &msg.records {
            rebuilt.extend_from_slice(&r.raw);
            rebuilt.push(b'\r');
        }
        assert_eq!(rebuilt, SAMPLE);
    }

    #[test]
    fn empty_message_errors() {
        assert_eq!(parse_message(b"", 4096), Err(RecordError::Empty));
        assert_eq!(parse_message(b"\r\r", 4096), Err(RecordError::Empty));
    }

    #[test]
    fn oversized_record_rejected() {
        let mut big = b"H|\\^&|\rR|".to_vec();
        big.extend_from_slice(&[b'x'; 100]);
        assert!(matches!(
            parse_message(&big, 10),
            Err(RecordError::RecordTooLong(_))
        ));
    }

    #[test]
    fn tolerates_crlf_line_endings() {
        let msg = parse_message(b"H|\\^&|\r\nL|1\r\n", 4096).unwrap();
        assert_eq!(msg.records.len(), 2);
        assert_eq!(msg.records[0].kind, RecordKind::Header);
        assert_eq!(msg.records[1].kind, RecordKind::Terminator);
    }

    #[test]
    fn fuzz_smoke_never_panics() {
        let mut state: u64 = 0x243F6A8885A308D3;
        let mut next = || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        for _ in 0..5000 {
            let len = (next() % 128) as usize;
            let bytes: Vec<u8> = (0..len).map(|_| (next() & 0xFF) as u8).collect();
            let _ = parse_message(&bytes, 4096);
        }
    }
}
