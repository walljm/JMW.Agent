// Command updatesign writes Ed25519 signature sidecars for agent update binaries.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/walljm/jmwagent/internal/shared/updatesig"
)

func main() {
	version := flag.String("version", "", "release version, e.g. v2.3.4")
	key := flag.String("key", os.Getenv("AGENT_UPDATE_SIGNING_KEY"), "base64 Ed25519 seed or private key")
	printPublic := flag.Bool("print-public-key", false, "print base64 public key derived from -key and exit")
	flag.Parse()

	if *key == "" {
		fatalf("AGENT_UPDATE_SIGNING_KEY or -key is required")
	}
	if *printPublic {
		publicKey, err := updatesig.PublicKeyBase64(*key)
		if err != nil {
			fatalf("derive public key: %v", err)
		}
		fmt.Println(publicKey)
		return
	}
	if *version == "" {
		fatalf("-version is required")
	}
	if flag.NArg() == 0 {
		fatalf("at least one binary path is required")
	}

	for _, binaryPath := range flag.Args() {
		meta, err := metadataFor(*version, binaryPath)
		if err != nil {
			fatalf("%s: %v", binaryPath, err)
		}
		sig, err := updatesig.Sign(meta, *key)
		if err != nil {
			fatalf("%s: sign: %v", binaryPath, err)
		}
		if err := os.WriteFile(binaryPath+".sig", []byte(sig+"\n"), 0o644); err != nil {
			fatalf("%s: write signature: %v", binaryPath, err)
		}
		fmt.Printf("signed %s\n", binaryPath)
	}
}

func metadataFor(version, binaryPath string) (updatesig.Metadata, error) {
	file, err := os.Open(binaryPath)
	if err != nil {
		return updatesig.Metadata{}, err
	}
	defer file.Close()

	hash := sha256.New()
	size, err := io.Copy(hash, file)
	if err != nil {
		return updatesig.Metadata{}, err
	}
	return updatesig.Metadata{
		Version:  version,
		Filename: filepath.Base(binaryPath),
		SHA256:   hex.EncodeToString(hash.Sum(nil)),
		Size:     size,
	}, nil
}

func fatalf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}
