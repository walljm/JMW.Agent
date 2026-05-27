package httpsrv

import (
	"net/http"

	"github.com/go-chi/chi/v5"
)

func (s *Server) hardwareListAPI(w http.ResponseWriter, r *http.Request) {
	items, err := s.Store.ListHardware(r.Context())
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	writeJSON(w, http.StatusOK, items)
}

func (s *Server) hardwareDetailAPI(w http.ResponseWriter, r *http.Request) {
	id := chi.URLParam(r, "id")
	detail, err := s.Store.GetHardwareDetail(r.Context(), id)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "db_error", err.Error())
		return
	}
	if detail == nil {
		writeJSONError(w, http.StatusNotFound, "not_found", "hardware not found")
		return
	}
	writeJSON(w, http.StatusOK, detail)
}
