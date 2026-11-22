package sqlserver

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"

	"github.com/temporalio/sqlparser"
	enumspb "go.temporal.io/api/enums/v1"
	p "go.temporal.io/server/common/persistence"
	"go.temporal.io/server/common/persistence/visibility/store/query"
	"go.temporal.io/server/common/searchattribute/sadefs"
)

// These tests need no database. The persistence suites (../../tests) need one.

const schemaRoot = "../../schema/sqlserver"

// The schema files, the version files, the latest versioned directory and the plugin's expected
// versions agree, and every statement passes Temporal's splitter and the update tool's prefix check.
func TestSchemaFilesAgree(t *testing.T) {
	for part, want := range map[string]string{"temporal": Version, "visibility": VisibilityVersion} {
		dir := filepath.Join(schemaRoot, part)
		v, err := os.ReadFile(filepath.Join(dir, "version"))
		if err != nil {
			t.Fatal(err)
		}
		if got := strings.TrimSpace(string(v)); got != want {
			t.Errorf("%s/version is %s, the plugin expects %s", part, got, want)
		}
		entries, err := os.ReadDir(filepath.Join(dir, "versioned"))
		if err != nil {
			t.Fatal(err)
		}
		var versions []string
		for _, e := range entries {
			versions = append(versions, strings.TrimPrefix(e.Name(), "v"))
		}
		sort.Strings(versions)
		latest := versions[len(versions)-1]
		if latest != want {
			t.Errorf("%s/versioned: latest is v%s, the plugin expects %s", part, latest, want)
		}
		var m struct{ CurrVersion string }
		raw, err := os.ReadFile(filepath.Join(dir, "versioned", "v"+latest, "manifest.json"))
		if err != nil {
			t.Fatal(err)
		}
		if err := json.Unmarshal(raw, &m); err != nil || m.CurrVersion != latest {
			t.Errorf("%s v%s manifest: CurrVersion %q, %v", part, latest, m.CurrVersion, err)
		}
		full, _ := os.ReadFile(filepath.Join(dir, "schema.sql"))
		versioned, _ := os.ReadFile(filepath.Join(dir, "versioned", "v"+latest, "schema.sql"))
		if string(full) != string(versioned) {
			t.Errorf("%s: schema.sql and versioned/v%s/schema.sql differ (the first SQL Server version is the whole schema)", part, latest)
		}
		stmts, err := p.LoadAndSplitQuery([]string{filepath.Join(dir, "schema.sql")})
		if err != nil {
			t.Fatalf("%s: %v", part, err)
		}
		for _, s := range stmts {
			if !strings.HasPrefix(s, "CREATE") && !strings.HasPrefix(s, "INSERT") {
				t.Errorf("%s: the schema tool takes CREATE, ALTER, INSERT, DROP or DO; got %.60q", part, s)
			}
		}
	}
}

func TestQueryConverterWritesTSQL(t *testing.T) {
	c := &queryConverter{}
	kw := query.NewSAColumn("Keyword01", "Keyword01", enumspb.INDEXED_VALUE_TYPE_KEYWORD)
	b := query.NewSAColumn("Bool01", "Bool01", enumspb.INDEXED_VALUE_TYPE_BOOL)
	kl := query.NewSAColumn("BuildIds", sadefs.BuildIds, enumspb.INDEXED_VALUE_TYPE_KEYWORD_LIST)
	txt := query.NewSAColumn("Text01", "Text01", enumspb.INDEXED_VALUE_TYPE_TEXT)

	check := func(e sqlparser.Expr, err error, want string) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
		if got := sqlparser.String(e); got != want {
			t.Errorf("got  %s\nwant %s", got, want)
		}
	}
	e, err := c.ConvertComparisonExpr("=", b, sqlparser.BoolVal(true))
	check(e, err, "Bool01 = 1")
	// The shared converter escapes as MySQL does; T-SQL takes the backslash literally.
	e, err = c.ConvertComparisonExpr("=", kw, query.NewUnsafeSQLString(`a\\b''c`))
	check(e, err, `Keyword01 = 'a\b''c'`)
	e, err = c.ConvertKeywordComparisonExpr(sqlparser.StartsWithStr, kw, query.NewUnsafeSQLString("50%_off"))
	check(e, err, `Keyword01 like '50!%!_off%' escape '!'`)
	e, err = c.ConvertKeywordListComparisonExpr("!=", kl, query.NewUnsafeSQLString("v1"))
	check(e, err, "NOT EXISTS (SELECT 1 FROM executions_visibility_keyword_lists kl WHERE kl.namespace_id = executions_visibility.namespace_id AND kl.run_id = executions_visibility.run_id AND kl.search_attribute = 'BuildIds' AND kl.value_hash IN (HASHBYTES('SHA2_256', CAST('v1' AS NVARCHAR(MAX)))))")
	e, err = c.ConvertTextComparisonExpr("=", txt, query.NewUnsafeSQLString("quick  fox"))
	check(e, err, "EXISTS (SELECT 1 FROM STRING_SPLIT(Text01, ' ') w WHERE w.value IN ('quick', 'fox'))")

	stmt, args := c.BuildSelectStmt(&query.QueryParams[sqlparser.Expr]{}, 10, nil)
	if !strings.HasSuffix(stmt, "ORDER BY close_time_or_max DESC, start_time DESC, run_id OFFSET 0 ROWS FETCH NEXT ? ROWS ONLY") || len(args) != 1 {
		t.Errorf("select: %s %v", stmt, args)
	}
}

// A list filter is a caller's text (the visibility API's query): every value it carries must reach T-SQL as one string
// literal, whatever quotes, backslashes or look-alike characters it holds (review 2026-11, SQL injection in the converter).
func TestQueryConverterKeepsEveryValueInsideOneLiteral(t *testing.T) {
	c := &queryConverter{}
	kw := query.NewSAColumn("Keyword01", "Keyword01", enumspb.INDEXED_VALUE_TYPE_KEYWORD)
	kl := query.NewSAColumn("BuildIds", sadefs.BuildIds, enumspb.INDEXED_VALUE_TYPE_KEYWORD_LIST)
	txt := query.NewSAColumn("Text01", "Text01", enumspb.INDEXED_VALUE_TYPE_TEXT)
	hostile := []string{
		`x'; DROP TABLE executions_visibility; --`,
		`x\'; DROP TABLE executions_visibility; --`,
		`x\\'' OR 1=1 --`,
		`''''`,
		`\`,
		"xʼ; DROP TABLE executions_visibility; --",
		"x\x00'; SELECT 1 --",
		`%_[!'`,
	}
	// One literal: an opening quote, then only doubled quotes until the closing one, which ends the text after prefix.
	literal := func(got, prefix, suffix string) bool {
		if !strings.HasPrefix(got, prefix+"'") || !strings.HasSuffix(got, "'"+suffix) || len(got) < len(prefix)+len(suffix)+2 {
			return false
		}
		body := got[len(prefix)+1 : len(got)-len(suffix)-1]
		return !strings.Contains(strings.ReplaceAll(body, "''", ""), "'")
	}
	for _, v := range hostile {
		e, err := c.ConvertComparisonExpr("=", kw, query.NewUnsafeSQLString(v))
		if err != nil || !literal(sqlparser.String(e), "Keyword01 = ", "") {
			t.Errorf("comparison of %q: %v %s", v, err, sqlparser.String(e))
		}
		e, err = c.ConvertKeywordComparisonExpr(sqlparser.StartsWithStr, kw, query.NewUnsafeSQLString(v))
		if err != nil || !literal(sqlparser.String(e), "Keyword01 like ", " escape '!'") {
			t.Errorf("starts-with %q: %v %s", v, err, sqlparser.String(e))
		}
		e, err = c.ConvertKeywordListComparisonExpr("=", kl, query.NewUnsafeSQLString(v))
		if err != nil {
			t.Fatalf("keyword list %q: %v", v, err)
		}
		got := sqlparser.String(e)
		inner := got[strings.Index(got, "CAST(")+len("CAST(") : strings.LastIndex(got, " AS NVARCHAR(MAX))")]
		if !literal(inner, "", "") {
			t.Errorf("keyword list %q: %s", v, got)
		}
		if e, err = c.ConvertTextComparisonExpr("=", txt, query.NewUnsafeSQLString(v)); err == nil {
			got := sqlparser.String(e)
			in := got[strings.Index(got, "w.value IN (")+len("w.value IN (") : len(got)-2]
			for _, lit := range strings.Split(in, ", ") {
				if !literal(lit, "", "") {
					t.Errorf("text %q: %s", v, got)
				}
			}
		}
	}
}

func TestRewriteLegacyLimit(t *testing.T) {
	if got := rewriteLegacyLimit("SELECT a FROM t ORDER BY a\n\t\tLIMIT ?"); got != "SELECT a FROM t ORDER BY a\n\t\tOFFSET 0 ROWS FETCH NEXT ? ROWS ONLY" {
		t.Errorf("got %q", got)
	}
}
