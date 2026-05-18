package httpsrv

import (
	"encoding/json"
	"net/http"
)

func (s *Server) terrainGet(w http.ResponseWriter, r *http.Request) {
	st := s.Terrain.Status()
	s.render(w, r, "terrain.html", map[string]any{
		"Title":  "Key Cyber Terrain",
		"Active": "terrain",
		"Status": st,
	})
}

func (s *Server) uiTerrainStatus(w http.ResponseWriter, r *http.Request) {
	st := s.Terrain.Status()
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(st)
}
