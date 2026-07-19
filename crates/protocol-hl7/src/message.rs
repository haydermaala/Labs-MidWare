//! HL7 v2 message parsing into a **lossless** structural representation.
//!
//! An HL7 v2 message is CR-separated segments; each segment is `|`-separated
//! fields; fields nest into repetitions (`~`), components (`^`), and
//! subcomponents (`&`). The delimiters are declared by the `MSH` segment
//! (`MSH-1` is the field separator, `MSH-2` the encoding characters `^~\&`).
//!
//! This layer only *structures* the bytes — it assigns **no clinical meaning**
//! (no unit/status/mapping inference; that is a later, gated step). Every level
//! keeps its raw bytes; unknown segments and extra fields are preserved. Parsing
//! never panics on malformed input.
//!
//! Note the well-known MSH quirk: because `MSH-1` *is* the field separator, when
//! a segment is split on that separator the encoding-characters field (`MSH-2`)
//! lands at index 1. Raw bytes are preserved, so nothing is lost.

use thiserror::Error;

/// HL7 delimiter set. Defaults are the standard `| ^ ~ \ &`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Delimiters {
    /// Field separator (default `|`).
    pub field: u8,
    /// Component separator (default `^`).
    pub component: u8,
    /// Repetition separator (default `~`).
    pub repetition: u8,
    /// Escape character (default `\`).
    pub escape: u8,
    /// Subcomponent separator (default `&`).
    pub subcomponent: u8,
}

impl Default for Delimiters {
    fn default() -> Self {
        Self {
            field: b'|',
            component: b'^',
            repetition: b'~',
            escape: b'\\',
            subcomponent: b'&',
        }
    }
}

impl Delimiters {
    /// Derive delimiters from an `MSH` segment's raw bytes. The byte after `MSH`
    /// is the field separator; the encoding-characters field that follows holds
    /// component, repetition, escape, and subcomponent characters in that order.
    #[must_use]
    pub fn from_msh(raw: &[u8]) -> Self {
        let d = Delimiters::default();
        let field = raw.get(3).copied().unwrap_or(d.field);
        let enc = raw
            .get(4..)
            .and_then(|rest| rest.split(|&b| b == field).next())
            .unwrap_or(&[]);
        Self {
            field,
            component: enc.first().copied().unwrap_or(d.component),
            repetition: enc.get(1).copied().unwrap_or(d.repetition),
            escape: enc.get(2).copied().unwrap_or(d.escape),
            subcomponent: enc.get(3).copied().unwrap_or(d.subcomponent),
        }
    }
}

/// A component: raw bytes plus its subcomponents.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Component {
    /// Raw bytes.
    pub raw: Vec<u8>,
    /// Subcomponent byte strings.
    pub subcomponents: Vec<Vec<u8>>,
}

/// A repetition: raw bytes plus its components.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Repetition {
    /// Raw bytes.
    pub raw: Vec<u8>,
    /// Components.
    pub components: Vec<Component>,
}

/// A field: raw bytes plus its repetitions.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Field {
    /// Raw bytes.
    pub raw: Vec<u8>,
    /// Repetitions (at least one).
    pub repetitions: Vec<Repetition>,
}

impl Field {
    /// The field's raw bytes as lossy UTF-8.
    #[must_use]
    pub fn text(&self) -> String {
        String::from_utf8_lossy(&self.raw).into_owned()
    }

    /// The 1-based `n`th component of the first repetition, as lossy UTF-8.
    #[must_use]
    pub fn component_text(&self, n: usize) -> Option<String> {
        self.repetitions
            .first()
            .and_then(|r| r.components.get(n.saturating_sub(1)))
            .map(|c| String::from_utf8_lossy(&c.raw).into_owned())
    }
}

/// A segment: its 3-character id, raw bytes, and fields (`fields[0]` is the id).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Segment {
    /// Segment id, e.g. "MSH", "PID", "OBX".
    pub id: String,
    /// Raw segment bytes (without the terminating `CR`).
    pub raw: Vec<u8>,
    /// Fields; `fields[0]` is the segment-id field.
    pub fields: Vec<Field>,
}

impl Segment {
    /// Borrow a field by index. For most segments index 1 is the first real
    /// field; for `MSH`, index 1 is the encoding-characters field (`MSH-2`).
    #[must_use]
    pub fn field(&self, index: usize) -> Option<&Field> {
        self.fields.get(index)
    }
}

/// A parsed HL7 v2 message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    /// The exact message bytes.
    pub raw: Vec<u8>,
    /// Delimiters resolved from `MSH` (or defaults).
    pub delimiters: Delimiters,
    /// The segments, in order.
    pub segments: Vec<Segment>,
}

impl Message {
    /// The first segment with the given id.
    #[must_use]
    pub fn segment(&self, id: &str) -> Option<&Segment> {
        self.segments.iter().find(|s| s.id == id)
    }

    /// All segments with the given id.
    pub fn segments_of<'a>(&'a self, id: &'a str) -> impl Iterator<Item = &'a Segment> + 'a {
        self.segments.iter().filter(move |s| s.id == id)
    }
}

/// Errors parsing an HL7 message.
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum Hl7Error {
    /// No segments were found.
    #[error("empty message")]
    Empty,
    /// The message did not start with an `MSH` segment.
    #[error("message does not start with MSH")]
    MissingMsh,
    /// A segment exceeded the configured maximum length.
    #[error("segment exceeds maximum length ({0} bytes)")]
    SegmentTooLong(usize),
}

fn split_on(bytes: &[u8], delim: u8) -> Vec<&[u8]> {
    bytes.split(move |&b| b == delim).collect()
}

fn parse_component(raw: &[u8], d: &Delimiters) -> Component {
    Component {
        raw: raw.to_vec(),
        subcomponents: split_on(raw, d.subcomponent)
            .into_iter()
            .map(<[u8]>::to_vec)
            .collect(),
    }
}

fn parse_repetition(raw: &[u8], d: &Delimiters) -> Repetition {
    Repetition {
        raw: raw.to_vec(),
        components: split_on(raw, d.component)
            .into_iter()
            .map(|c| parse_component(c, d))
            .collect(),
    }
}

fn parse_field(raw: &[u8], d: &Delimiters) -> Field {
    Field {
        raw: raw.to_vec(),
        repetitions: split_on(raw, d.repetition)
            .into_iter()
            .map(|r| parse_repetition(r, d))
            .collect(),
    }
}

/// Parse a single segment line (no trailing `CR`) with the given delimiters.
#[must_use]
pub fn parse_segment(line: &[u8], delimiters: &Delimiters) -> Segment {
    let fields: Vec<Field> = split_on(line, delimiters.field)
        .into_iter()
        .map(|f| parse_field(f, delimiters))
        .collect();
    let id = line
        .split(|&b| b == delimiters.field)
        .next()
        .map(|id| String::from_utf8_lossy(id).into_owned())
        .unwrap_or_default();
    Segment {
        id,
        raw: line.to_vec(),
        fields,
    }
}

/// Parse a whole HL7 v2 message. Segments are `CR`-separated (`LF` tolerated).
/// Delimiters come from the leading `MSH` segment. `max_segment_len` bounds each
/// segment.
pub fn parse_message(bytes: &[u8], max_segment_len: usize) -> Result<Message, Hl7Error> {
    let lines: Vec<&[u8]> = bytes
        .split(|&b| b == b'\r')
        .map(|line| {
            let line = line.strip_prefix(b"\n").unwrap_or(line);
            line.strip_suffix(b"\n").unwrap_or(line)
        })
        .filter(|line| !line.is_empty())
        .collect();

    if lines.is_empty() {
        return Err(Hl7Error::Empty);
    }
    if !lines[0].starts_with(b"MSH") {
        return Err(Hl7Error::MissingMsh);
    }

    let delimiters = Delimiters::from_msh(lines[0]);

    let mut segments = Vec::with_capacity(lines.len());
    for line in lines {
        if line.len() > max_segment_len {
            return Err(Hl7Error::SegmentTooLong(line.len()));
        }
        segments.push(parse_segment(line, &delimiters));
    }

    Ok(Message {
        raw: bytes.to_vec(),
        delimiters,
        segments,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    // Synthetic ORU^R01 (no real patient data).
    const ORU: &[u8] = b"MSH|^~\\&|ANALYZER|LAB|LIS|HOSP|20260718100000||ORU^R01|MSG00001|P|2.5.1\rPID|1||SYNTH-PID^^^LAB||SYNTHETIC^PATIENT||19700101|U\rOBR|1|ORD-1|FILL-1|^^^GLU^Glucose\rOBX|1|NM|^^^GLU^Glucose||5.30|mmol/L|3.9-5.6|N|||F\r";

    #[test]
    fn parses_segments_in_order() {
        let msg = parse_message(ORU, 4096).unwrap();
        let ids: Vec<_> = msg.segments.iter().map(|s| s.id.as_str()).collect();
        assert_eq!(ids, vec!["MSH", "PID", "OBR", "OBX"]);
    }

    #[test]
    fn derives_delimiters_from_msh() {
        let msg = parse_message(ORU, 4096).unwrap();
        assert_eq!(
            msg.delimiters,
            Delimiters {
                field: b'|',
                component: b'^',
                repetition: b'~',
                escape: b'\\',
                subcomponent: b'&',
            }
        );
    }

    #[test]
    fn message_type_is_at_msh_9() {
        let msg = parse_message(ORU, 4096).unwrap();
        let msh = msg.segment("MSH").unwrap();
        // MSH-9 lands at split index 8 (MSH-1 is the separator).
        let msg_type = msh.field(8).unwrap();
        assert_eq!(msg_type.component_text(1).as_deref(), Some("ORU"));
        assert_eq!(msg_type.component_text(2).as_deref(), Some("R01"));
    }

    #[test]
    fn obx_value_and_components_are_preserved() {
        let msg = parse_message(ORU, 4096).unwrap();
        let obx = msg.segment("OBX").unwrap();
        // OBX-5 (observation value) preserved exactly.
        assert_eq!(obx.field(5).unwrap().text(), "5.30");
        assert_eq!(obx.field(6).unwrap().text(), "mmol/L");
        // OBX-3 "^^^GLU^Glucose" → 5 components.
        let obs_id = obx.field(3).unwrap();
        assert_eq!(obs_id.repetitions[0].components.len(), 5);
        assert_eq!(obs_id.component_text(4).as_deref(), Some("GLU"));
        assert_eq!(obs_id.component_text(5).as_deref(), Some("Glucose"));
    }

    #[test]
    fn repetitions_and_subcomponents_split() {
        let d = Delimiters::default();
        // A field "a&b^c~d" → 2 repetitions; first rep comp0 has 2 subcomponents.
        let seg = parse_segment(b"OBX|a&b^c~d", &d);
        let f = seg.field(1).unwrap();
        assert_eq!(f.repetitions.len(), 2);
        assert_eq!(
            f.repetitions[0].components[0].subcomponents,
            vec![b"a".to_vec(), b"b".to_vec()]
        );
        assert_eq!(f.repetitions[1].components[0].raw, b"d");
    }

    #[test]
    fn unknown_segment_is_preserved() {
        let msg = parse_message(b"MSH|^~\\&|x\rZZZ|vendor|custom\r", 4096).unwrap();
        assert_eq!(msg.segments[1].id, "ZZZ");
        assert_eq!(msg.segments[1].raw, b"ZZZ|vendor|custom");
    }

    #[test]
    fn requires_msh_and_rejects_empty() {
        assert_eq!(parse_message(b"", 4096), Err(Hl7Error::Empty));
        assert_eq!(parse_message(b"PID|1\r", 4096), Err(Hl7Error::MissingMsh));
    }

    #[test]
    fn oversized_segment_rejected() {
        let mut big = b"MSH|^~\\&|\rOBX|".to_vec();
        big.extend_from_slice(&[b'x'; 100]);
        assert!(matches!(
            parse_message(&big, 10),
            Err(Hl7Error::SegmentTooLong(_))
        ));
    }

    #[test]
    fn fuzz_smoke_never_panics() {
        let mut state: u64 = 0xDEADBEEFCAFEBABE;
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
