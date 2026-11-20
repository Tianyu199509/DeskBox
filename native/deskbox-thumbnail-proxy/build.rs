use std::env;
use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-changed=DeskBox.ThumbnailProxy.manifest");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
        let manifest_path = manifest_dir.join("DeskBox.ThumbnailProxy.manifest");
        println!("cargo:rustc-link-arg-bin=deskbox-thumbnail-proxy=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bin=deskbox-thumbnail-proxy=/MANIFESTUAC:NO");
        println!(
            "cargo:rustc-link-arg-bin=deskbox-thumbnail-proxy=/MANIFESTINPUT:{}",
            manifest_path.display()
        );
    }
}
