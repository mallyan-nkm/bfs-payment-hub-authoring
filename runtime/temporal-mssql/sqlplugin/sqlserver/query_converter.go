package sqlserver

import (
	"fmt"
	"strings"

	"github.com/temporalio/sqlparser"
	"go.temporal.io/server/common/persistence/sql/sqlplugin"
	"go.temporal.io/server/common/persistence/visibility/store/query"
	"go.temporal.io/server/common/searchattribute/sadefs"
)

// The visibility query converter for SQL Server. Temporal's unified converter
// (common/persistence/visibility/store/sql) parses the list filter and calls these hooks for the
// parts that differ by dialect:
//
//   - A Keyword List attribute is a JSON array in search_attributes. A trigger keeps one row per
//     (execution, attribute, value) in executions_visibility_keyword_lists, keyed on the SHA-256 of
//     the value, so `BuildIds = 'x'` is an indexed EXISTS on that table.
//   - A Text attribute matches when any token of the query is a word of the text, as PostgreSQL's
//     tsvector match does (no stemming, case-sensitive). SQL Server has no full-text index in the
//     Linux container image, so this is a STRING_SPLIT scan of the candidate rows.
//   - T-SQL has no TRUE or FALSE: a Bool attribute compares with 1 or 0.
//   - T-SQL string literals take no backslash escapes, so the backslash escapes the shared
//     converter adds are undone ('' stays).
//   - The sort key COALESCE(close_time, '9999-12-31 23:59:59') is the persisted, indexed column
//     close_time_or_max.

const (
	keywordListTable      = "executions_visibility_keyword_lists"
	closeTimeOrMaxColumn  = "close_time_or_max"
	visibilityTable       = "executions_visibility"
	textTokenSeparator    = " "
	sqlServerDatetimeForm = "2006-01-02 15:04:05.999999"
)

// rawExpr is an expression written out as T-SQL text.
type rawExpr struct {
	sqlparser.Expr
	sql string
}

var _ sqlparser.Expr = (*rawExpr)(nil)

func (node *rawExpr) Format(buf *sqlparser.TrackedBuffer) {
	buf.WriteString(node.sql)
}

type queryConverter struct{}

var _ sqlplugin.VisibilityQueryConverter = (*queryConverter)(nil)

func (c *queryConverter) GetDatetimeFormat() string {
	return sqlServerDatetimeForm
}

func (c *queryConverter) GetCoalesceCloseTimeExpr() sqlparser.Expr {
	return &query.ColumnName{Name: closeTimeOrMaxColumn}
}

// ConvertComparisonExpr writes a comparison with T-SQL literals.
func (c *queryConverter) ConvertComparisonExpr(
	operator string,
	col *query.SAColumn,
	value sqlparser.Expr,
) (sqlparser.Expr, error) {
	return &sqlparser.ComparisonExpr{Operator: operator, Left: col, Right: tsqlValue(value)}, nil
}

// ConvertKeywordComparisonExpr is the shared converter's keyword comparison (with STARTS_WITH as
// LIKE ... ESCAPE '!') with T-SQL literals.
func (c *queryConverter) ConvertKeywordComparisonExpr(
	operator string,
	col *query.SAColumn,
	value sqlparser.Expr,
) (sqlparser.Expr, error) {
	switch operator {
	case sqlparser.StartsWithStr, sqlparser.NotStartsWithStr:
		v, ok := value.(*query.UnsafeSQLString)
		if !ok {
			return nil, query.NewConverterError(
				"%s: right-hand side of %q operator must be a literal string (got: %v)",
				query.InvalidExpressionErrMessage, operator, sqlparser.String(value))
		}
		pattern := escapeLike(unescape(v.Val), '!')
		op := sqlparser.LikeStr
		if operator == sqlparser.NotStartsWithStr {
			op = sqlparser.NotLikeStr
		}
		return &sqlparser.ComparisonExpr{
			Operator: op,
			Left:     col,
			Right:    &rawExpr{sql: tsqlString(pattern)},
			Escape:   &rawExpr{sql: "'!'"},
		}, nil
	default:
		return c.ConvertComparisonExpr(operator, col, value)
	}
}

func (c *queryConverter) ConvertRangeExpr(
	operator string,
	col *query.SAColumn,
	from, to sqlparser.Expr,
) (sqlparser.Expr, error) {
	return &sqlparser.RangeCond{Operator: operator, Left: col, From: tsqlValue(from), To: tsqlValue(to)}, nil
}

func (c *queryConverter) ConvertKeywordListComparisonExpr(
	operator string,
	col *query.SAColumn,
	value sqlparser.Expr,
) (sqlparser.Expr, error) {
	var values sqlparser.ValTuple
	switch operator {
	case sqlparser.EqualStr, sqlparser.NotEqualStr:
		values = sqlparser.ValTuple{value}
	case sqlparser.InStr, sqlparser.NotInStr:
		tuple, ok := value.(sqlparser.ValTuple)
		if !ok {
			return nil, query.NewConverterError(
				"%s: unexpected value type (expected tuple of strings, got %s)",
				query.InvalidExpressionErrMessage, sqlparser.String(value))
		}
		values = tuple
	default:
		// isSupportedKeywordListOperator has already refused any other operator.
		return nil, query.NewConverterError(
			"%s: operator '%s' not supported for KeywordList type",
			query.InvalidExpressionErrMessage, operator)
	}
	hashes := make([]string, len(values))
	for i, v := range values {
		s, ok := v.(*query.UnsafeSQLString)
		if !ok {
			return nil, query.NewConverterError(
				"%s: unexpected value type (expected string, got %s)",
				query.InvalidExpressionErrMessage, sqlparser.String(v))
		}
		hashes[i] = fmt.Sprintf("HASHBYTES('SHA2_256', CAST(%s AS NVARCHAR(MAX)))", tsqlString(unescape(s.Val)))
	}
	exists := fmt.Sprintf(
		"EXISTS (SELECT 1 FROM %[1]s kl WHERE kl.namespace_id = %[2]s.namespace_id AND kl.run_id = %[2]s.run_id AND kl.search_attribute = %[3]s AND kl.value_hash IN (%[4]s))",
		keywordListTable, visibilityTable, tsqlString(sadefs.GetSqlDbColName(col.FieldName)), strings.Join(hashes, ", "))
	if operator == sqlparser.NotEqualStr || operator == sqlparser.NotInStr {
		exists = "NOT " + exists
	}
	return &rawExpr{sql: exists}, nil
}

func (c *queryConverter) ConvertTextComparisonExpr(
	operator string,
	col *query.SAColumn,
	value sqlparser.Expr,
) (sqlparser.Expr, error) {
	valueExpr, ok := value.(*query.UnsafeSQLString)
	if !ok {
		return nil, query.NewConverterError(
			"%s: unexpected value type (expected string, got %s)",
			query.InvalidExpressionErrMessage, sqlparser.String(value))
	}
	tokens := query.TokenizeTextQueryString(unescape(valueExpr.Val))
	if len(tokens) == 0 {
		return nil, query.NewConverterError(
			"%s: unexpected value for Text type search attribute (no tokens found)",
			query.InvalidExpressionErrMessage)
	}
	literals := make([]string, len(tokens))
	for i, t := range tokens {
		literals[i] = tsqlString(t)
	}
	match := fmt.Sprintf("EXISTS (SELECT 1 FROM STRING_SPLIT(%s, %s) w WHERE w.value IN (%s))",
		sqlparser.String(col), tsqlString(textTokenSeparator), strings.Join(literals, ", "))
	if operator == sqlparser.NotEqualStr {
		match = "NOT " + match
	}
	return &rawExpr{sql: match}, nil
}

func (c *queryConverter) BuildSelectStmt(
	queryParams *query.QueryParams[sqlparser.Expr],
	pageSize int,
	token *sqlplugin.VisibilityPageToken,
) (string, []any) {
	var whereClauses []string
	var queryArgs []any

	if queryParams.QueryExpr != nil {
		if queryString := sqlparser.String(queryParams.QueryExpr); queryString != "" {
			whereClauses = append(whereClauses, queryString)
		}
	}

	if token != nil {
		whereClauses = append(
			whereClauses,
			fmt.Sprintf(
				"((%s = ? AND %s = ? AND %s > ?) OR (%s = ? AND %s < ?) OR %s < ?)",
				closeTimeOrMaxColumn,
				sadefs.GetSqlDbColName(sadefs.StartTime),
				sadefs.GetSqlDbColName(sadefs.RunID),
				closeTimeOrMaxColumn,
				sadefs.GetSqlDbColName(sadefs.StartTime),
				closeTimeOrMaxColumn,
			),
		)
		queryArgs = append(queryArgs,
			token.CloseTime, token.StartTime, token.RunID,
			token.CloseTime, token.StartTime,
			token.CloseTime,
		)
	}

	whereString := ""
	if len(whereClauses) > 0 {
		whereString = " WHERE " + strings.Join(whereClauses, " AND ")
	}

	stmt := fmt.Sprintf(
		`SELECT %s FROM executions_visibility%s ORDER BY %s DESC, %s DESC, %s OFFSET 0 ROWS FETCH NEXT ? ROWS ONLY`,
		strings.Join(sqlplugin.DbFields, ", "),
		whereString,
		closeTimeOrMaxColumn,
		sadefs.GetSqlDbColName(sadefs.StartTime),
		sadefs.GetSqlDbColName(sadefs.RunID),
	)
	queryArgs = append(queryArgs, pageSize)
	return stmt, queryArgs
}

func (c *queryConverter) BuildCountStmt(
	queryParams *query.QueryParams[sqlparser.Expr],
) (string, []any) {
	whereString := ""
	if queryParams.QueryExpr != nil {
		whereString = sqlparser.String(queryParams.QueryExpr)
		if whereString != "" {
			whereString = " WHERE " + whereString
		}
	}

	groupBy := make([]string, 0, len(queryParams.GroupBy)+1)
	for _, field := range queryParams.GroupBy {
		groupBy = append(groupBy, sadefs.GetSqlDbColName(field.FieldName))
	}

	groupByClause := ""
	if len(queryParams.GroupBy) > 0 {
		groupByClause = fmt.Sprintf(" GROUP BY %s", strings.Join(groupBy, ", "))
	}

	return fmt.Sprintf(
		"SELECT %s FROM executions_visibility%s%s",
		strings.Join(append(groupBy, "COUNT_BIG(*)"), ", "),
		whereString,
		groupByClause,
	), nil
}

// tsqlValue rewrites a literal for T-SQL: a Bool as 1 or 0, a string without backslash escapes.
func tsqlValue(e sqlparser.Expr) sqlparser.Expr {
	switch v := e.(type) {
	case sqlparser.BoolVal:
		if v {
			return sqlparser.NewIntVal([]byte("1"))
		}
		return sqlparser.NewIntVal([]byte("0"))
	case *query.UnsafeSQLString:
		return &rawExpr{sql: tsqlString(unescape(v.Val))}
	case sqlparser.ValTuple:
		out := make(sqlparser.ValTuple, len(v))
		for i, item := range v {
			out[i] = tsqlValue(item)
		}
		return out
	default:
		return e
	}
}

// unescape undoes the shared converter's escaping of a string value (which writes MySQL escapes),
// back to the plain value. A quote doubled for SQL (”) is undone too; tsqlString doubles it again.
func unescape(s string) string {
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		ch := s[i]
		switch {
		case ch == '\\' && i+1 < len(s):
			i++
			switch s[i] {
			case 'b':
				b.WriteByte('\b')
			case 'n':
				b.WriteByte('\n')
			case 'r':
				b.WriteByte('\r')
			case 't':
				b.WriteByte('\t')
			default: // \\ and \"
				b.WriteByte(s[i])
			}
		case ch == '\'' && i+1 < len(s) && s[i+1] == '\'':
			b.WriteByte('\'')
			i++
		default:
			b.WriteByte(ch)
		}
	}
	return b.String()
}

// tsqlString writes a T-SQL string literal. It is VARCHAR in the database collation, which is UTF-8,
// so it holds any character and compares with the VARCHAR columns without a conversion.
func tsqlString(s string) string {
	return "'" + strings.ReplaceAll(s, "'", "''") + "'"
}

func escapeLike(s string, escape byte) string {
	var b strings.Builder
	for _, r := range s {
		if r == '%' || r == '_' || r == '[' || r == rune(escape) {
			b.WriteByte(escape)
		}
		b.WriteRune(r)
	}
	b.WriteByte('%')
	return b.String()
}
