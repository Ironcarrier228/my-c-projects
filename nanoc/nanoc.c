/* ============================================================
 *  NanoC — компилятор с языка Nano в исполняемые файлы Windows
 *  (x86-64, машинный код и PE-заголовки генерируются вручную)
 *
 *  Использование:  nanoc.exe program.nano program.exe
 *
 *  Язык Nano:
 *    let x = 5;                  // объявление переменной
 *    x = x + 1;                  // присваивание
 *    print x * 2;                // печать числа
 *    println "текст";            // печать строки (+ перевод строки)
 *    if  (x > 3) { ... } else { ... }
 *    while (x > 0) { x = x - 1; }
 *    // комментарии так
 * ============================================================ */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ============================================================
 *  Динамический буфер
 * ============================================================ */
typedef struct { unsigned char *d; int n, cap; } Buf;

static void buf_need(Buf *b, int extra)
{
    if (b->n + extra > b->cap) {
        b->cap = (b->n + extra) * 2 + 1024;
        b->d = realloc(b->d, b->cap);
        if (!b->d) { fprintf(stderr, "nanoc: нет памяти\n"); exit(1); }
    }
}
static void emit8(Buf *b, unsigned v)  { buf_need(b, 1); b->d[b->n++] = (unsigned char)v; }
static void emit16(Buf *b, unsigned v) { emit8(b, v); emit8(b, v >> 8); }
static void emit32(Buf *b, unsigned v) { emit16(b, v); emit16(b, v >> 16); }
static void emit64(Buf *b, unsigned long long v)
{
    int i; for (i = 0; i < 8; i++) emit8(b, (unsigned)(v >> (8 * i)));
}
static void emit_str(Buf *b, const char *s) { while (*s) emit8(b, *s++); }

/* ============================================================
 *  Токенайзер
 * ============================================================ */
static char *src;
static int   pos = 0;
static int   line = 1;

static void fail(const char *msg)
{
    fprintf(stderr, "nanoc: строка %d: %s\n", line, msg);
    exit(1);
}

enum {
    T_EOF = 0, T_NUM, T_ID, T_STR,
    T_LET, T_PRINT, T_PRINTLN, T_IF, T_ELSE, T_WHILE,
    T_ASSIGN, T_SEMI, T_LP, T_RP, T_LBR, T_RBR,
    T_PLUS, T_MINUS, T_STAR, T_SLASH, T_PERCENT,
    T_EQ, T_NE, T_LT, T_GT, T_LE, T_GE
};

static int   tok;
static long  tok_num;
static char  tok_id[64];
static char *tok_str;
static int   tok_str_len;

static void next_tok(void)
{
    for (;;) {
        char c = src[pos];
        if (c == '\n') { line++; pos++; continue; }
        if (c == ' ' || c == '\t' || c == '\r') { pos++; continue; }
        if (c == '/' && src[pos + 1] == '/') {
            while (src[pos] && src[pos] != '\n') pos++;
            continue;
        }
        break;
    }

    char c = src[pos];
    if (!c) { tok = T_EOF; return; }

    if (isdigit((unsigned char)c)) {
        tok_num = 0;
        while (isdigit((unsigned char)src[pos])) {
            tok_num = tok_num * 10 + (src[pos] - '0');
            pos++;
        }
        tok = T_NUM;
        return;
    }

    if (isalpha((unsigned char)c) || c == '_') {
        int n = 0;
        while (isalnum((unsigned char)src[pos]) || src[pos] == '_') {
            if (n < 63) tok_id[n++] = src[pos];
            pos++;
        }
        tok_id[n] = 0;
        if      (!strcmp(tok_id, "let"))     tok = T_LET;
        else if (!strcmp(tok_id, "print"))   tok = T_PRINT;
        else if (!strcmp(tok_id, "println")) tok = T_PRINTLN;
        else if (!strcmp(tok_id, "if"))      tok = T_IF;
        else if (!strcmp(tok_id, "else"))    tok = T_ELSE;
        else if (!strcmp(tok_id, "while"))   tok = T_WHILE;
        else                                 tok = T_ID;
        return;
    }

    if (c == '"') {
        pos++;
        int cap = 32, n = 0;
        tok_str = malloc(cap);
        while (src[pos] && src[pos] != '"') {
            if (n + 2 > cap) { cap *= 2; tok_str = realloc(tok_str, cap); }
            tok_str[n++] = src[pos++];
        }
        if (src[pos] != '"') fail("ожидалась закрывающая кавычка");
        pos++;
        tok_str[n] = 0;
        tok_str_len = n;
        tok = T_STR;
        return;
    }

    pos++;
    switch (c) {
    case ';': tok = T_SEMI;    return;
    case '(': tok = T_LP;      return;
    case ')': tok = T_RP;      return;
    case '{': tok = T_LBR;     return;
    case '}': tok = T_RBR;     return;
    case '+': tok = T_PLUS;    return;
    case '-': tok = T_MINUS;   return;
    case '*': tok = T_STAR;    return;
    case '/': tok = T_SLASH;   return;
    case '%': tok = T_PERCENT; return;
    case '=': if (src[pos] == '=') { pos++; tok = T_EQ; } else tok = T_ASSIGN; return;
    case '!': if (src[pos] == '=') { pos++; tok = T_NE; } else fail("неизвестный символ '!'"); return;
    case '<': if (src[pos] == '=') { pos++; tok = T_LE; } else tok = T_LT; return;
    case '>': if (src[pos] == '=') { pos++; tok = T_GE; } else tok = T_GT; return;
    }
    fail("неизвестный символ");
}

static void expect(int t, const char *what)
{
    if (tok != t) fail(what);
    next_tok();
}

/* ============================================================
 *  Переменные
 * ============================================================ */
#define MAX_VARS 40
static char var_names[MAX_VARS][64];
static int  var_count = 0;

static int var_slot(const char *name)   /* слот: rbp - 0x90 - 8*(i+1) */
{
    int i;
    for (i = 0; i < var_count; i++)
        if (!strcmp(var_names[i], name)) return i;
    if (var_count >= MAX_VARS) fail("слишком много переменных (максимум 40)");
    strncpy(var_names[var_count], name, 63);
    return var_count++;
}

/* ============================================================
 *  Кодогенератор
 * ============================================================ */
static Buf code;

#define MAX_LABELS 4096
static int  label_pos[MAX_LABELS];
static char label_used[MAX_LABELS];
static struct { int label; int pos; } jmp_fix[16384];
static int  jmp_fix_n = 0;
static int  label_next = 0;
static int  itoa_label = -1;

static int new_label(void)
{
    if (label_next >= MAX_LABELS) fail("слишком много меток");
    label_used[label_next] = 0;
    return label_next++;
}

static void place_label(int l)
{
    label_pos[l] = code.n;
    label_used[l] = 1;
    int i;
    for (i = 0; i < jmp_fix_n; i++) {
        if (jmp_fix[i].label == l) {
            int rel = label_pos[l] - (jmp_fix[i].pos + 4);
            memcpy(code.d + jmp_fix[i].pos, &rel, 4);
            jmp_fix[i].label = -1;
        }
    }
}

static void emit_jmp(int opcode, int label)   /* 0xE9 = jmp, иначе 0F 8x = jcc */
{
    if (opcode == 0xE9) {
        emit8(&code, 0xE9);
    } else {
        emit8(&code, 0x0F);
        emit8(&code, (unsigned char)opcode);
    }
    if (jmp_fix_n >= 16384) fail("слишком много переходов");
    jmp_fix[jmp_fix_n].label = label;
    jmp_fix[jmp_fix_n].pos = code.n;
    jmp_fix_n++;
    emit32(&code, 0);
}

#define MAX_STRS 2048
static struct { char *text; int len; int offset; } strs[MAX_STRS];
static int str_count = 0;

static int add_string(const char *s, int len)
{
    if (str_count >= MAX_STRS) fail("слишком много строк");
    strs[str_count].text = malloc(len + 1);
    memcpy(strs[str_count].text, s, len);
    strs[str_count].text[len] = 0;
    strs[str_count].len = len;
    strs[str_count].offset = -1;
    return str_count++;
}

static struct { int pos; int str_index; } str_fix[16384];
static int str_fix_n = 0;

static void emit_load_str_addr_rdx(int str_index)   /* movabs rdx, addr */
{
    if (str_fix_n >= 16384) fail("слишком много строк");
    str_fix[str_fix_n].pos = code.n + 2;
    str_fix[str_fix_n].str_index = str_index;
    str_fix_n++;
    emit8(&code, 0x48); emit8(&code, 0xBA);
    emit64(&code, 0);
}

enum { IMP_WRITEFILE, IMP_EXITPROCESS, IMP_COUNT };
static struct { int pos; int imp; } imp_fix[8192];
static int imp_fix_n = 0;

static void emit_call_import(int imp)           /* movabs rax, &IAT; call [rax] */
{
    if (imp_fix_n >= 8192) fail("слишком много вызовов");
    imp_fix[imp_fix_n].pos = code.n + 2;
    imp_fix[imp_fix_n].imp = imp;
    imp_fix_n++;
    emit8(&code, 0x48); emit8(&code, 0xB8);
    emit64(&code, 0);
    emit8(&code, 0xFF); emit8(&code, 0x10);     /* call qword [rax] */
}

static void emit_load_str_addr_rcx(int str_index)   /* movabs rcx, addr */
{
    if (str_fix_n >= 16384) fail("slishkom mnogo strok");
    str_fix[str_fix_n].pos = code.n + 2;
    str_fix[str_fix_n].str_index = str_index;
    str_fix_n++;
    emit8(&code, 0x48); emit8(&code, 0xB9);
    emit64(&code, 0);
}

static void gen_expr(void);
static void gen_stmt(void);
static void gen_write_crlf(void);
static void gen_block(void);

/* ============================================================
 *  Печать: WriteFile(hStdout, ...) — работает и в консоль, и в pipe
 * ============================================================ */

/* значение уже в rax: собственный itoa + WriteFile */
static void gen_print_int_from_rax(int newline)
{
    emit8(&code, 0x48); emit8(&code, 0x8D); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x50);                 /* lea rcx, [rbp-0x50] (конец буфера) */
    emit_jmp(0xE8, itoa_label);                     /* call itoa (rel32) */
    /* rax = длина, rdx = старт цифр */
    emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x80);                 /* mov rcx, [rbp-0x80] (hStdout) */
    emit8(&code, 0x44); emit8(&code, 0x8B); emit8(&code, 0xC0); /* mov r8d, eax (длина) */
    emit8(&code, 0x4C); emit8(&code, 0x8D); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x78);                 /* lea r9, [rbp-0x78] */
    emit8(&code, 0x48); emit8(&code, 0xC7); emit8(&code, 0x44); emit8(&code, 0x24);
    emit8(&code, 0x20); emit32(&code, 0);           /* lpOverlapped = NULL (arg5 = [rsp+0x20]) */
    emit_call_import(IMP_WRITEFILE);

    if (newline) gen_write_crlf();
}

static void gen_print_int(int newline)
{
    gen_expr();
    expect(T_SEMI, "ожидается ';'");
    gen_print_int_from_rax(newline);
}

static void gen_write_crlf(void)
{
    emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x80);                 /* mov rcx, [rbp-0x80] (hStdout) */
    emit_load_str_addr_rdx(add_string("\r\n", 2));
    emit8(&code, 0x41); emit8(&code, 0xB8); emit32(&code, 2);   /* mov r8d, 2 */
    emit8(&code, 0x4C); emit8(&code, 0x8D); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x78);                 /* lea r9, [rbp-0x78] */
    emit8(&code, 0x48); emit8(&code, 0xC7); emit8(&code, 0x44); emit8(&code, 0x24);
    emit8(&code, 0x20); emit32(&code, 0);           /* lpOverlapped = NULL (arg5 = [rsp+0x20]) */
    emit_call_import(IMP_WRITEFILE);
}

static void gen_print_str(int str_index, int len, int newline)
{
    emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x80);                 /* mov rcx, [rbp-0x80] (hStdout) */
    emit_load_str_addr_rdx(str_index);
    emit8(&code, 0x41); emit8(&code, 0xB8); emit32(&code, (unsigned)len);
    emit8(&code, 0x4C); emit8(&code, 0x8D); emit8(&code, 0x8D);
    emit32(&code, (unsigned)-0x78);                 /* lea r9, [rbp-0x78] */
    emit8(&code, 0x48); emit8(&code, 0xC7); emit8(&code, 0x44); emit8(&code, 0x24);
    emit8(&code, 0x20); emit32(&code, 0);           /* lpOverlapped = NULL (arg5 = [rsp+0x20]) */
    emit_call_import(IMP_WRITEFILE);
    if (newline) gen_write_crlf();
}

/* ============================================================
 *  Выражения (результат в RAX)
 * ============================================================ */
static void gen_factor(void)
{
    if (tok == T_NUM) {
        long v = tok_num;
        next_tok();
        emit8(&code, 0x48); emit8(&code, 0xB8);
        emit64(&code, (unsigned long long)v);
        return;
    }
    if (tok == T_ID) {
        int slot = var_slot(tok_id);
        next_tok();
        emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x85);
        emit32(&code, (unsigned)(-0x90 - 8 * (slot + 1)));  /* mov rax, [rbp-..] */
        return;
    }
    if (tok == T_LP) {
        next_tok();
        gen_expr();
        expect(T_RP, "ожидается ')'");
        return;
    }
    if (tok == T_MINUS) {
        next_tok();
        gen_factor();
        emit8(&code, 0x48); emit8(&code, 0xF7); emit8(&code, 0xD8); /* neg rax */
        return;
    }
    fail("ожидалось число, переменная или '('");
}

static void gen_term(void)
{
    gen_factor();
    while (tok == T_STAR || tok == T_SLASH || tok == T_PERCENT) {
        int op = tok;
        next_tok();
        emit8(&code, 0x50);                 /* push rax */
        gen_factor();
        emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0xC1); /* mov rcx, rax */
        emit8(&code, 0x58);                 /* pop rax */
        if (op == T_STAR) {
            emit8(&code, 0x48); emit8(&code, 0xF7); emit8(&code, 0xE9); /* imul rcx */
        } else {
            emit8(&code, 0x48); emit8(&code, 0x99);                     /* cqo */
            emit8(&code, 0x48); emit8(&code, 0xF7); emit8(&code, 0xF9); /* idiv rcx */
            if (op == T_PERCENT)
                emit8(&code, 0x48), emit8(&code, 0x89), emit8(&code, 0xD0); /* mov rax, rdx */
        }
    }
}

static void gen_additive(void)
{
    gen_term();
    while (tok == T_PLUS || tok == T_MINUS) {
        int op = tok;
        next_tok();
        emit8(&code, 0x50);                 /* push rax */
        gen_term();
        emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0xC1); /* mov rcx, rax */
        emit8(&code, 0x58);                 /* pop rax */
        if (op == T_PLUS)
            emit8(&code, 0x48), emit8(&code, 0x01), emit8(&code, 0xC8); /* add rax, rcx */
        else
            emit8(&code, 0x48), emit8(&code, 0x29), emit8(&code, 0xC8); /* sub rax, rcx */
    }
}

static void gen_compare(void)
{
    gen_additive();
    while (tok == T_EQ || tok == T_NE || tok == T_LT || tok == T_GT || tok == T_LE || tok == T_GE) {
        int op = tok;
        next_tok();
        emit8(&code, 0x50);                 /* push rax */
        gen_additive();
        emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0xC8); /* mov rax, rcx */
        emit8(&code, 0x58);                 /* pop rax */
        emit8(&code, 0x48); emit8(&code, 0x39); emit8(&code, 0xC8); /* cmp rax, rcx */
        unsigned setcc;
        switch (op) {
        case T_EQ: setcc = 0x94; break;     /* sete  */
        case T_NE: setcc = 0x95; break;     /* setne */
        case T_LT: setcc = 0x9C; break;     /* setl  */
        case T_GT: setcc = 0x9F; break;     /* setg  */
        case T_LE: setcc = 0x9E; break;     /* setle */
        default:   setcc = 0x9D; break;     /* setge */
        }
        emit8(&code, 0x0F); emit8(&code, setcc); emit8(&code, 0xC0);        /* setcc al */
        emit8(&code, 0x48); emit8(&code, 0x0F); emit8(&code, 0xB6); emit8(&code, 0xC0); /* movzx rax, al */
    }
}

static void gen_expr(void) { gen_compare(); }

/* ============================================================
 *  Операторы
 * ============================================================ */
static void gen_store_var(int slot)
{
    emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0x85);
    emit32(&code, (unsigned)(-0x90 - 8 * (slot + 1)));
}

static void gen_block(void)
{
    expect(T_LBR, "ожидается '{'");
    while (tok != T_RBR && tok != T_EOF)
        gen_stmt();
    expect(T_RBR, "ожидается '}'");
}

static void gen_stmt(void)
{
    if (tok == T_LET) {
        next_tok();
        if (tok != T_ID) fail("ожидается имя переменной");
        int slot = var_slot(tok_id);
        next_tok();
        expect(T_ASSIGN, "ожидается '=' после имени");
        gen_expr();
        expect(T_SEMI, "ожидается ';'");
        gen_store_var(slot);
        return;
    }

    if (tok == T_ID) {
        int slot = var_slot(tok_id);
        next_tok();
        expect(T_ASSIGN, "ожидается '='");
        gen_expr();
        expect(T_SEMI, "ожидается ';'");
        gen_store_var(slot);
        return;
    }

    if (tok == T_PRINT || tok == T_PRINTLN) {
        int nl = (tok == T_PRINTLN);
        next_tok();
        if (tok == T_STR) {
            int si = add_string(tok_str, tok_str_len);
            next_tok();
            expect(T_SEMI, "ожидается ';'");
            gen_print_str(si, strs[si].len, nl);
        } else {
            gen_print_int(nl);
        }
        return;
    }

    if (tok == T_IF) {
        next_tok();
        expect(T_LP, "ожидается '(' после if");
        gen_expr();
        expect(T_RP, "ожидается ')'");
        int l_false = new_label(), l_end = new_label();
        emit8(&code, 0x48); emit8(&code, 0x85); emit8(&code, 0xC0); /* test rax, rax */
        emit_jmp(0x84, l_false);                /* jz */
        if (tok == T_LBR) gen_block(); else gen_stmt();
        if (tok == T_ELSE) {
            next_tok();
            emit_jmp(0xE9, l_end);
            place_label(l_false);
            if (tok == T_LBR) gen_block(); else gen_stmt();
            place_label(l_end);
        } else {
            place_label(l_false);
        }
        return;
    }

    if (tok == T_WHILE) {
        next_tok();
        expect(T_LP, "ожидается '(' после while");
        int l_start = new_label();
        place_label(l_start);
        gen_expr();
        expect(T_RP, "ожидается ')'");
        int l_end = new_label();
        emit8(&code, 0x48); emit8(&code, 0x85); emit8(&code, 0xC0);
        emit_jmp(0x84, l_end);
        if (tok == T_LBR) gen_block(); else gen_stmt();
        emit_jmp(0xE9, l_start);
        place_label(l_end);
        return;
    }

    fail("неизвестный оператор (ожидалось let/print/println/if/while)");
}

/* ============================================================
 *  Сборка PE
 * ============================================================ */
static const char *imp_names[IMP_COUNT] = {
    "WriteFile", "ExitProcess"
};
static const char *imp_dlls[2] = { "kernel32.dll", "user32.dll" };
static int imp_dll_of[IMP_COUNT] = { 0, 0 };

static void write_pe(const char *outpath)
{
    Buf pe = { 0, 0, 0 };
    int i, fi;

    for (i = 0; i < str_count; i++) {
        while (code.n % 8) emit8(&code, 0);
        strs[i].offset = code.n;
        { int k; for (k = 0; k < strs[i].len; k++) emit8(&code, (unsigned char)strs[i].text[k]); }
        emit8(&code, 0);
    }

        int text_size = code.n;
    int text_raw  = (text_size + 0x1FF) & ~0x1FF;
    int idata_va  = 0x200 + text_raw;
        int idata_raw;

    Buf id = { 0, 0, 0 };
    int dir_off = id.n;
    { int k; for (k = 0; k < 60; k++) emit8(&id, 0); }

        int hn_off[IMP_COUNT];
    for (fi = 0; fi < IMP_COUNT; fi++) {
        hn_off[fi] = id.n;
        emit8(&id, 0); emit8(&id, 0);
        emit_str(&id, imp_names[fi]);
        emit8(&id, 0);
    }
        int dllname_off[2];
    for (i = 0; i < 2; i++) {
        dllname_off[i] = id.n;
        emit_str(&id, imp_dlls[i]);
        emit8(&id, 0);
        if (id.n % 2) emit8(&id, 0);
    }

        int ilt_off[2], iat_off[2];
    for (i = 0; i < 2; i++) {
        ilt_off[i] = id.n;
        for (fi = 0; fi < IMP_COUNT; fi++)
            if (imp_dll_of[fi] == i) emit64(&id, (unsigned long long)(idata_va + hn_off[fi]));
        emit64(&id, 0);
        iat_off[i] = id.n;
        for (fi = 0; fi < IMP_COUNT; fi++)
            if (imp_dll_of[fi] == i) emit64(&id, (unsigned long long)(idata_va + hn_off[fi]));
        emit64(&id, 0);
    }

        {
        int o = dir_off;
        int dll;
        for (dll = 0; dll < 2; dll++) {
            unsigned v;
            v = (unsigned)(idata_va + ilt_off[dll]);  memcpy(id.d + o + 0,  &v, 4);
            v = 0;                                    memcpy(id.d + o + 4,  &v, 4);
            v = 0;                                    memcpy(id.d + o + 8,  &v, 4);
            v = (unsigned)(idata_va + dllname_off[dll]); memcpy(id.d + o + 12, &v, 4);
            v = (unsigned)(idata_va + iat_off[dll]);  memcpy(id.d + o + 16, &v, 4);
            o += 20;
        }
    }

    idata_raw = (id.n + 0x1FF) & ~0x1FF;
    int idata_size = id.n;

    {
        unsigned long long base = 0x400000ULL;
                for (i = 0; i < str_fix_n; i++) {
                        unsigned long long addr = base + 0x200 + (unsigned)strs[str_fix[i].str_index].offset;
            memcpy(code.d + str_fix[i].pos, &addr, 8);
        }
                int slot_of[IMP_COUNT];
        int seen[2] = { 0, 0 };
        for (fi = 0; fi < IMP_COUNT; fi++) {
            int dll = imp_dll_of[fi];
            slot_of[fi] = iat_off[dll] + 8 * seen[dll];
            seen[dll]++;
        }
                for (i = 0; i < imp_fix_n; i++) {
            unsigned long long addr = base + (unsigned)(idata_va + slot_of[imp_fix[i].imp]);
            memcpy(code.d + imp_fix[i].pos, &addr, 8);
        }
                for (i = 0; i < jmp_fix_n; i++) {
            if (jmp_fix[i].label >= 0 && label_used[jmp_fix[i].label]) {
                int rel = label_pos[jmp_fix[i].label] - (jmp_fix[i].pos + 4);
                memcpy(code.d + jmp_fix[i].pos, &rel, 4);
            }
        }
    }

        int size_of_image = ((idata_va + idata_raw) + 0x1FF) & ~0x1FF;
    if (size_of_image < 0x1000) size_of_image = 0x1000;

    emit_str(&pe, "MZ");
    { int k; for (k = 2; k < 0x40; k++) emit8(&pe, 0); }
    pe.d[0x3C] = 0x80;
    {
        static const unsigned char stub[64] = {
            0x0E,0x1F,0xBA,0x0E,0x00,0xB4,0x09,0xCD,0x21,0xB8,0x01,0x4C,0xCD,0x21,
            'T','h','i','s',' ','p','r','o','g','r','a','m',' ','c','a','n','n','o','t',' ',
            'b','e',' ','r','u','n',' ','i','n',' ','D','O','S',' ','m','o','d','e','.',' ',
            0x0D,0x0D,0x0A,'$',0x00,0x00,0x00,0x00,0x00,0x00
        };
        int k; for (k = 0; k < 64; k++) emit8(&pe, stub[k]);
    }
    while (pe.n < 0x80) emit8(&pe, 0);

    emit_str(&pe, "PE"); emit8(&pe, 0); emit8(&pe, 0);
    emit16(&pe, 0x8664);
    emit16(&pe, 2);
    emit32(&pe, 0); emit32(&pe, 0); emit32(&pe, 0);
    emit16(&pe, 0xF0);
    emit16(&pe, 0x0022);

    emit16(&pe, 0x20B);
    emit8(&pe, 1); emit8(&pe, 0);
    emit32(&pe, (unsigned)text_raw);
    emit32(&pe, (unsigned)idata_raw);
    emit32(&pe, 0);
    emit32(&pe, 0x200);
    emit32(&pe, 0x200);
    emit64(&pe, 0x400000);
    emit32(&pe, 0x200);
    emit32(&pe, 0x200);
    emit16(&pe, 6); emit16(&pe, 0);
    emit16(&pe, 0); emit16(&pe, 0);
    emit16(&pe, 6); emit16(&pe, 0);
    emit32(&pe, 0);
    emit32(&pe, (unsigned)size_of_image);
    emit32(&pe, 0x200);
    emit32(&pe, 0);
    emit16(&pe, 3);
    emit16(&pe, 0);
    emit64(&pe, 0x100000);
    emit64(&pe, 0x1000);
    emit64(&pe, 0x100000);
    emit64(&pe, 0x1000);
    emit32(&pe, 0);
    emit32(&pe, 16);
    { int k; for (k = 0; k < 16; k++) {
        if (k == 1) { emit32(&pe, (unsigned)idata_va); emit32(&pe, (unsigned)idata_size); }
        else { emit32(&pe, 0); emit32(&pe, 0); }
    } }

        emit_str(&pe, ".text"); emit8(&pe, 0); emit8(&pe, 0); emit8(&pe, 0);
    emit32(&pe, (unsigned)text_size);
    emit32(&pe, 0x200);
    emit32(&pe, (unsigned)text_raw);
    emit32(&pe, 0x200);
    emit32(&pe, 0);                                 /* PointerToRelocations */
    emit32(&pe, 0);                                 /* PointerToLinenumbers */
    emit16(&pe, 0); emit16(&pe, 0);
    emit32(&pe, 0x60000020);
    emit_str(&pe, ".idata"); emit8(&pe, 0); emit8(&pe, 0);
    emit32(&pe, (unsigned)idata_size);
    emit32(&pe, (unsigned)idata_va);
    emit32(&pe, (unsigned)idata_raw);
    emit32(&pe, (unsigned)(0x200 + text_raw));
    emit32(&pe, 0); emit32(&pe, 0);
    emit16(&pe, 0); emit16(&pe, 0);
    emit32(&pe, 0xC0000040);
    while (pe.n < 0x200) emit8(&pe, 0);

    { int k; for (k = 0; k < text_size; k++) emit8(&pe, code.d[k]); }
    while (pe.n < 0x200 + text_raw) emit8(&pe, 0);
    { int k; for (k = 0; k < idata_size; k++) emit8(&pe, id.d[k]); }

        FILE *fp = fopen(outpath, "wb");
    if (!fp) { fprintf(stderr, "nanoc: не могу записать %s\n", outpath); exit(1); }
    fwrite(pe.d, 1, pe.n, fp);
    fclose(fp);
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "NanoC v0.1 - ispolzovanie: nanoc <program.nano> <vyhod.exe>\n");
        return 1;
    }

    FILE *f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "nanoc: ne mogu otkryt %s\n", argv[1]); return 1; }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    src = malloc(size + 1);
    if (fread(src, 1, size, f) != (size_t)size) { fprintf(stderr, "nanoc: oshibka chteniya\n"); return 1; }
    src[size] = 0;
    fclose(f);

    itoa_label = new_label();

    /* Prolog: kadr steka + handle stdout iz PEB */
    emit8(&code, 0x55);                             /* push rbp */
    emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0xE5); /* mov rbp, rsp */
    emit8(&code, 0x48); emit8(&code, 0x83); emit8(&code, 0xE4); emit8(&code, 0xF0); /* and rsp, -16 */
    emit8(&code, 0x48); emit8(&code, 0x81); emit8(&code, 0xEC);
    emit32(&code, 0x200);                           /* sub rsp, 0x200 */
    emit8(&code, 0x65); emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x04);
    emit8(&code, 0x25); emit32(&code, 0x60);        /* mov rax, gs:[0x60] (PEB) */
    emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x40); emit8(&code, 0x20); /* mov rax,[rax+0x20] ProcessParameters */
    emit8(&code, 0x48); emit8(&code, 0x8B); emit8(&code, 0x40); emit8(&code, 0x28); /* mov eax,[rax+0x1C] StandardOutput */
    emit8(&code, 0x48); emit8(&code, 0x89); emit8(&code, 0x85);
    emit32(&code, (unsigned)-0x80);                 /* mov [rbp-0x80], rax */

        next_tok();
    while (tok != T_EOF) {
                gen_stmt();
    }
    
    /* Epilog: ExitProcess(0) */
    emit8(&code, 0x31); emit8(&code, 0xC9);
    emit_call_import(IMP_EXITPROCESS);

    /* --- itoa: rcx = конец буфера, rax = значение; возврат: rax = длина, rdx = старт --- */
    place_label(itoa_label);
    emit8(&code, 0x49); emit8(&code, 0x89); emit8(&code, 0xC8);     /* mov r8, rcx (end) */
    emit8(&code, 0x49); emit8(&code, 0xC7); emit8(&code, 0xC1);
    emit32(&code, 10);                                              /* mov r9, 10 */
    emit8(&code, 0x48); emit8(&code, 0x85); emit8(&code, 0xC0);     /* test rax, rax */
    { int l_nz = new_label();
      emit_jmp(0x85, l_nz);
      emit8(&code, 0x48); emit8(&code, 0xFF); emit8(&code, 0xC9);   /* dec rcx */
      emit8(&code, 0xC6); emit8(&code, 0x01); emit8(&code, 0x30);   /* mov byte [rcx], 48 */
      { int l_done = new_label(); emit_jmp(0xE9, l_done); place_label(l_done); }
      place_label(l_nz);
      { int l_loop = new_label();
        place_label(l_loop);
        emit8(&code, 0x48); emit8(&code, 0x31); emit8(&code, 0xD2); /* xor rdx, rdx */
        emit8(&code, 0x49); emit8(&code, 0xF7); emit8(&code, 0xF1); /* div r9 */
        emit8(&code, 0x48); emit8(&code, 0x83); emit8(&code, 0xC2); emit8(&code, 0x30); /* add rdx, 48 */
        emit8(&code, 0x48); emit8(&code, 0xFF); emit8(&code, 0xC9); /* dec rcx */
        emit8(&code, 0x88); emit8(&code, 0x11);                     /* mov [rcx], dl */
        emit8(&code, 0x48); emit8(&code, 0x85); emit8(&code, 0xC0); /* test rax, rax */
        emit_jmp(0x85, l_loop);
      }
    }
    emit8(&code, 0x4C); emit8(&code, 0x89); emit8(&code, 0xCA);     /* mov rdx, rcx (start) */
    emit8(&code, 0x4C); emit8(&code, 0x89); emit8(&code, 0xC0);     /* mov rax, r8 (end) */
    emit8(&code, 0x4C); emit8(&code, 0x29); emit8(&code, 0xC8);     /* sub rax, rcx (len) */
    emit8(&code, 0xC3);                                             /* ret */

    write_pe(argv[2]);
    printf("nanoc: %s -> %s (%d bayt mashinnogo koda)\n", argv[1], argv[2], code.n);
    return 0;
}
