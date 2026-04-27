package smoke

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
)

func doRequest(method, url, psk string, body, out any) error {
	var br io.Reader
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			return err
		}
		br = bytes.NewReader(b)
	}
	req, err := http.NewRequest(method, url, br)
	if err != nil {
		return err
	}
	if br != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if psk != "" {
		req.Header.Set("X-Agent-PSK", psk)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		b, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("status %d: %s", resp.StatusCode, string(b))
	}
	if out == nil {
		return nil
	}
	return json.NewDecoder(resp.Body).Decode(out)
}
