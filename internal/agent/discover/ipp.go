package discover

import (
	"bytes"
	"context"
	"encoding/binary"
	"io"
	"net"
	"net/http"
	"strings"
	"time"
)

// IPPInfo is what we extract from a printer's IPP Get-Printer-Attributes
// response. Almost every modern printer answers IPP on port 631, even
// when it doesn't advertise via mDNS or has a sparse SNMP MIB.
type IPPInfo struct {
	Name     string `json:"name,omitempty"`     // printer-name
	Make     string `json:"make,omitempty"`     // printer-make-and-model
	Info     string `json:"info,omitempty"`     // printer-info
	Location string `json:"location,omitempty"` // printer-location
}

// ippProbe issues a minimal Get-Printer-Attributes request to ip:631 and
// parses the response for the four most identifying string attributes.
// Returns nil for non-printers, timeouts, or parse failures.
//
// IPP wire format (RFC 8011 §4.1):
//   - 2 bytes  version (1.1 -> 0x01 0x01)
//   - 2 bytes  operation-id (Get-Printer-Attributes = 0x000B)
//   - 4 bytes  request-id
//   - 1 byte   delimiter tag (operation-attributes-tag = 0x01)
//   - attribute groups (each: tag + name-len + name + value-len + value)
//   - 1 byte   end-of-attributes-tag (0x03)
//
// Required operation attributes:
//   - attributes-charset (tag 0x47): "utf-8"
//   - attributes-natural-language (tag 0x48): "en"
//   - printer-uri (tag 0x45): "ipp://<ip>/ipp/print"
func ippProbe(ip string, timeout time.Duration) *IPPInfo {
	if ip == "" {
		return nil
	}
	body := buildIPPGetPrinterAttributes(ip)

	urlStr := "http://" + net.JoinHostPort(ip, "631") + "/ipp/print"
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, "POST", urlStr, bytes.NewReader(body))
	if err != nil {
		return nil
	}
	req.Header.Set("Content-Type", "application/ipp")
	cli := &http.Client{Timeout: timeout, Transport: &http.Transport{DisableKeepAlives: true}}
	resp, err := cli.Do(req)
	if err != nil {
		return nil
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil
	}
	respBody, _ := io.ReadAll(io.LimitReader(resp.Body, 65536))
	info := parseIPPAttributes(respBody)
	if info == nil {
		return nil
	}
	if info.Name == "" && info.Make == "" && info.Info == "" {
		return nil
	}
	return info
}

func buildIPPGetPrinterAttributes(ip string) []byte {
	var b bytes.Buffer
	// Header: version + op-id + request-id.
	b.Write([]byte{0x01, 0x01, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01})
	// Operation-attributes-tag.
	b.WriteByte(0x01)
	ippAttr(&b, 0x47, "attributes-charset", "utf-8")
	ippAttr(&b, 0x48, "attributes-natural-language", "en")
	ippAttr(&b, 0x45, "printer-uri", "ipp://"+ip+"/ipp/print")
	// Requested attributes (one per repeated entry — same tag with empty
	// name continues the previous attribute).
	ippAttr(&b, 0x44, "requested-attributes", "printer-name")
	ippAttr(&b, 0x44, "", "printer-make-and-model")
	ippAttr(&b, 0x44, "", "printer-info")
	ippAttr(&b, 0x44, "", "printer-location")
	// End-of-attributes-tag.
	b.WriteByte(0x03)
	return b.Bytes()
}

// ippAttr appends a single IPP attribute: tag, name (length-prefixed),
// value (length-prefixed). Empty name is a continuation of the previous
// attribute (used for multi-value requested-attributes).
func ippAttr(b *bytes.Buffer, tag byte, name, value string) {
	b.WriteByte(tag)
	var ln [2]byte
	binary.BigEndian.PutUint16(ln[:], uint16(len(name)))
	b.Write(ln[:])
	b.WriteString(name)
	binary.BigEndian.PutUint16(ln[:], uint16(len(value)))
	b.Write(ln[:])
	b.WriteString(value)
}

// parseIPPAttributes walks an IPP response and returns the printer
// identity fields. Returns nil if the response is too short or doesn't
// look like IPP.
func parseIPPAttributes(buf []byte) *IPPInfo {
	if len(buf) < 9 {
		return nil
	}
	// Skip 8-byte header. Status-code (bytes 2-3) is informational; we
	// don't fail the parse on non-success because some printers return
	// useful attributes alongside warning codes.
	off := 8
	info := &IPPInfo{}
	var lastName string
	for off < len(buf) {
		tag := buf[off]
		off++
		// Delimiter tags (0x00-0x05) introduce a new group.
		if tag <= 0x05 {
			if tag == 0x03 { // end-of-attributes
				break
			}
			lastName = ""
			continue
		}
		if off+2 > len(buf) {
			break
		}
		nameLen := int(binary.BigEndian.Uint16(buf[off : off+2]))
		off += 2
		if off+nameLen > len(buf) {
			break
		}
		name := string(buf[off : off+nameLen])
		off += nameLen
		if off+2 > len(buf) {
			break
		}
		valLen := int(binary.BigEndian.Uint16(buf[off : off+2]))
		off += 2
		if off+valLen > len(buf) {
			break
		}
		val := string(buf[off : off+valLen])
		off += valLen
		// Empty-name attributes are additional values of the prior
		// attribute (multi-valued attributes). Roll the name forward.
		if name == "" {
			name = lastName
		} else {
			lastName = name
		}
		v := strings.TrimSpace(val)
		if v == "" {
			continue
		}
		switch name {
		case "printer-name":
			if info.Name == "" {
				info.Name = v
			}
		case "printer-make-and-model":
			if info.Make == "" {
				info.Make = v
			}
		case "printer-info":
			if info.Info == "" {
				info.Info = v
			}
		case "printer-location":
			if info.Location == "" {
				info.Location = v
			}
		}
	}
	return info
}
