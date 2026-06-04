// Package version exposes build-time version info.
package version

// Version is set at build time via -ldflags.
var Version = "dev"

// UpdatePublicKey is the base64 Ed25519 public key used to verify agent
// self-update signatures. Set at build time via -ldflags; overridden by
// update_public_key in agent.toml if non-empty.
var UpdatePublicKey = ""
