package store

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"time"

	"golang.org/x/crypto/bcrypt"
)

// hashToken returns the hex-encoded SHA-256 hash of a raw session token.
// Tokens are hashed before storage so that a database compromise does not
// directly expose valid session credentials.
func hashToken(token string) string {
	h := sha256.Sum256([]byte(token))
	return hex.EncodeToString(h[:])
}

// User represents an authenticated dashboard user.
type User struct {
	ID           int64
	Username     string
	PasswordHash string
	CreatedAt    time.Time
}

// CountUsers returns the number of users in the system. Used to detect first boot.
func (s *Store) CountUsers(ctx context.Context) (int, error) {
	var n int
	if err := s.DB.QueryRowContext(ctx, `SELECT COUNT(*) FROM users`).Scan(&n); err != nil {
		return 0, err
	}
	return n, nil
}

// CreateUser hashes the password and inserts a new user.
func (s *Store) CreateUser(ctx context.Context, username, password string) (*User, error) {
	if username == "" || len(password) < 6 {
		return nil, fmt.Errorf("username required and password must be at least 6 characters")
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(password), 12)
	if err != nil {
		return nil, err
	}
	now := time.Now().UTC().Format(time.RFC3339)
	res, err := s.DB.ExecContext(ctx,
		`INSERT INTO users(username, password_hash, created_at) VALUES(?,?,?)`,
		username, string(hash), now)
	if err != nil {
		return nil, err
	}
	id, _ := res.LastInsertId()
	return &User{ID: id, Username: username, PasswordHash: string(hash)}, nil
}

// AuthenticateUser checks credentials and returns the user on match.
func (s *Store) AuthenticateUser(ctx context.Context, username, password string) (*User, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, username, password_hash, created_at FROM users WHERE username = ?`,
		username)
	var u User
	var createdStr string
	if err := row.Scan(&u.ID, &u.Username, &u.PasswordHash, &createdStr); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrInvalidCredentials
		}
		return nil, err
	}
	u.CreatedAt, _ = time.Parse(time.RFC3339, createdStr)
	if err := bcrypt.CompareHashAndPassword([]byte(u.PasswordHash), []byte(password)); err != nil {
		return nil, ErrInvalidCredentials
	}
	return &u, nil
}

// GetUserByID returns a user by ID.
func (s *Store) GetUserByID(ctx context.Context, id int64) (*User, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, username, password_hash, created_at FROM users WHERE id = ?`, id)
	var u User
	var createdStr string
	if err := row.Scan(&u.ID, &u.Username, &u.PasswordHash, &createdStr); err != nil {
		return nil, err
	}
	u.CreatedAt, _ = time.Parse(time.RFC3339, createdStr)
	return &u, nil
}

// ErrInvalidCredentials is returned when login fails.
var ErrInvalidCredentials = errors.New("invalid credentials")

// Session represents a logged-in session.
type Session struct {
	ID         string
	UserID     int64
	CreatedAt  time.Time
	ExpiresAt  time.Time
	LastUsedAt time.Time
}

// CreateSession creates a session for the given user.
// The returned Session.ID is the raw token (for the cookie); only the
// SHA-256 hash is persisted to the database so that a DB compromise does
// not expose valid session tokens.
func (s *Store) CreateSession(ctx context.Context, userID int64, lifetime time.Duration) (*Session, error) {
	rawToken, err := randomToken(32)
	if err != nil {
		return nil, err
	}
	hashed := hashToken(rawToken)
	now := time.Now().UTC()
	exp := now.Add(lifetime)
	if _, err := s.DB.ExecContext(ctx,
		`INSERT INTO sessions(id, user_id, created_at, expires_at, last_used_at) VALUES(?,?,?,?,?)`,
		hashed, userID, now.Format(time.RFC3339), exp.Format(time.RFC3339), now.Format(time.RFC3339)); err != nil {
		return nil, err
	}
	// Return the raw token so callers can set it as the cookie value.
	return &Session{ID: rawToken, UserID: userID, CreatedAt: now, ExpiresAt: exp, LastUsedAt: now}, nil
}

// GetSession returns a session by raw token, only if it has not expired.
// The raw token is hashed before querying the database.
// NOTE: this intentionally invalidates any pre-existing sessions that were
// stored with unhashed tokens (a one-time forced logout for security).
func (s *Store) GetSession(ctx context.Context, rawToken string) (*Session, error) {
	hashed := hashToken(rawToken)
	row := s.DB.QueryRowContext(ctx,
		`SELECT id, user_id, created_at, expires_at, last_used_at FROM sessions WHERE id = ?`, hashed)
	var sess Session
	var c, e, l string
	if err := row.Scan(&sess.ID, &sess.UserID, &c, &e, &l); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	sess.CreatedAt, _ = time.Parse(time.RFC3339, c)
	sess.ExpiresAt, _ = time.Parse(time.RFC3339, e)
	sess.LastUsedAt, _ = time.Parse(time.RFC3339, l)
	if time.Now().UTC().After(sess.ExpiresAt) {
		_ = s.DeleteSession(ctx, rawToken)
		return nil, nil
	}
	return &sess, nil
}

// TouchSession bumps last_used_at. Accepts the raw token and hashes it
// before querying.
func (s *Store) TouchSession(ctx context.Context, rawToken string) error {
	hashed := hashToken(rawToken)
	_, err := s.DB.ExecContext(ctx,
		`UPDATE sessions SET last_used_at = ? WHERE id = ?`,
		time.Now().UTC().Format(time.RFC3339), hashed)
	return err
}

// DeleteSession removes a session. Accepts the raw token and hashes it
// before querying.
func (s *Store) DeleteSession(ctx context.Context, rawToken string) error {
	hashed := hashToken(rawToken)
	_, err := s.DB.ExecContext(ctx, `DELETE FROM sessions WHERE id = ?`, hashed)
	return err
}

// PurgeExpiredSessions removes all expired sessions.
func (s *Store) PurgeExpiredSessions(ctx context.Context) (int64, error) {
	res, err := s.DB.ExecContext(ctx,
		`DELETE FROM sessions WHERE expires_at < ?`,
		time.Now().UTC().Format(time.RFC3339))
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func randomToken(byteLen int) (string, error) {
	b := make([]byte, byteLen)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}
