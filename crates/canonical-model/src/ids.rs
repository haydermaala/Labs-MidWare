//! Strongly typed entity identifiers.
//!
//! Each identifier is a distinct newtype over a UUID so a `ResultId` can never be
//! passed where a `SpecimenId` is expected. IDs serialize transparently as their
//! UUID string.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

macro_rules! typed_id {
    ($(#[$meta:meta])* $name:ident) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(Uuid);

        impl $name {
            /// Generate a fresh random (v4) identifier.
            #[must_use]
            pub fn new() -> Self {
                Self(Uuid::new_v4())
            }

            /// Wrap an existing UUID.
            #[must_use]
            pub const fn from_uuid(uuid: Uuid) -> Self {
                Self(uuid)
            }

            /// Borrow the underlying UUID.
            #[must_use]
            pub const fn as_uuid(&self) -> Uuid {
                self.0
            }
        }

        impl Default for $name {
            fn default() -> Self {
                Self::new()
            }
        }

        impl core::fmt::Display for $name {
            fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
                core::fmt::Display::fmt(&self.0, f)
            }
        }
    };
}

typed_id!(
    /// Identifies a raw, unparsed device message (the root of every provenance chain).
    RawMessageId
);
typed_id!(
    /// Identifies a parsed (but not yet normalized) message.
    ParsedMessageId
);
typed_id!(
    /// Identifies a single normalized result.
    ResultId
);
typed_id!(
    /// Identifies a set of results reported together.
    ResultSetId
);
typed_id!(
    /// Identifies a specimen.
    SpecimenId
);
typed_id!(
    /// Identifies a laboratory order.
    LabOrderId
);
typed_id!(
    /// Identifies a requested test within an order.
    RequestedTestId
);
typed_id!(
    /// Identifies a configured device instance at a site.
    DeviceInstanceId
);
typed_id!(
    /// Identifies a gateway.
    GatewayId
);
typed_id!(
    /// Identifies a specific driver version.
    DriverVersionId
);
typed_id!(
    /// Identifies a specific mapping version.
    MappingVersionId
);
typed_id!(
    /// Identifies a single delivery attempt to a downstream system.
    DeliveryAttemptId
);
typed_id!(
    /// Identifies an acknowledgement received from a downstream system.
    AcknowledgementId
);
typed_id!(
    /// Identifies a validation run.
    ValidationRunId
);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_are_distinct_and_roundtrip() {
        let id = ResultId::new();
        let json = serde_json::to_string(&id).unwrap();
        // Transparent: serializes as a bare UUID string.
        assert_eq!(json, format!("\"{}\"", id.as_uuid()));
        let back: ResultId = serde_json::from_str(&json).unwrap();
        assert_eq!(id, back);
    }

    #[test]
    fn from_uuid_preserves_value() {
        let raw = Uuid::new_v4();
        assert_eq!(SpecimenId::from_uuid(raw).as_uuid(), raw);
    }
}
