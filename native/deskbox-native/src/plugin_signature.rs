//! Signature verification only. Signing keys never enter the host.
use ed25519_dalek::{Signature, VerifyingKey};

/// # Safety
/// Non-null input pointers must reference their declared readable lengths.
/// valid must point to one writable u32. No buffers are retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn deskbox_plugin_verify_ed25519_v1(
    signature: *const u8,
    signature_len: u32,
    message: *const u8,
    message_len: u32,
    public_key: *const u8,
    public_key_len: u32,
    valid: *mut u32,
) -> u32 {
    if valid.is_null() {
        return crate::DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    unsafe { *valid = 0 };
    if signature_len != 64
        || public_key_len != 32
        || message_len > 1024 * 1024
        || signature.is_null()
        || public_key.is_null()
        || (message_len != 0 && message.is_null())
    {
        return crate::DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    let signature = unsafe { &*(signature as *const [u8; 64]) };
    let public_bytes = unsafe { &*(public_key as *const [u8; 32]) };
    let message = if message_len == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(message, message_len as usize) }
    };
    if let Ok(key) = VerifyingKey::from_bytes(public_bytes) {
        // The package format rejects non-canonical public-key encodings.
        let canonical = key.to_edwards().compress().to_bytes() == *public_bytes;
        let accepted = canonical
            && key
                .verify_strict(message, &Signature::from_bytes(signature))
                .is_ok();
        unsafe { *valid = u32::from(accepted) };
    }
    crate::DESKBOX_NATIVE_STATUS_OK
}

#[cfg(test)]
mod tests {
    use super::*;
    use ed25519_dalek::{Signer, SigningKey};

    #[test]
    fn ffi_accepts_valid_and_rejects_tampered_signatures() {
        let key = SigningKey::from_bytes(&[7; 32]);
        let public = key.verifying_key().to_bytes();
        let message = b"DeskBox package";
        let mut signature = key.sign(message).to_bytes();
        let mut valid = 0;
        assert_eq!(
            unsafe {
                deskbox_plugin_verify_ed25519_v1(
                    signature.as_ptr(),
                    64,
                    message.as_ptr(),
                    message.len() as u32,
                    public.as_ptr(),
                    32,
                    &mut valid,
                )
            },
            0
        );
        assert_eq!(valid, 1);
        signature[0] ^= 1;
        assert_eq!(
            unsafe {
                deskbox_plugin_verify_ed25519_v1(
                    signature.as_ptr(),
                    64,
                    message.as_ptr(),
                    message.len() as u32,
                    public.as_ptr(),
                    32,
                    &mut valid,
                )
            },
            0
        );
        assert_eq!(valid, 0);
    }

    #[test]
    fn ffi_rejects_null_and_invalid_lengths() {
        let mut valid = 1;
        assert_eq!(
            unsafe {
                deskbox_plugin_verify_ed25519_v1(
                    std::ptr::null(),
                    64,
                    std::ptr::null(),
                    0,
                    std::ptr::null(),
                    32,
                    &mut valid,
                )
            },
            crate::DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT
        );
        assert_eq!(valid, 0);
    }
}
