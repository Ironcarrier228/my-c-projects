/* ============================================================
 *  PONG — классика 1972 года, Си + PDCurses (ncurses-API)
 *  Эталон: ponggame.org
 *
 *  Управление:
 *    Игрок 1 (левая ракетка):  W / S
 *    Игрок 2 (правая ракетка): стрелки ВВЕРХ / ВНИЗ (или ИИ)
 *    M — меню, R — рестарт, Q — выход
 *
 *  Сборка (zig cc + PDCurses):
 *    zig cc -O2 -o Pong.exe pong.c pdcurses/*.c wincon/*.c \
 *        -I. -Iwincon -luser32 -lgdi32
 * ============================================================ */

#include <curses.h>
#include <windows.h>
#include <stdlib.h>
#include <time.h>

/* Русский текст в UTF-8 -> кодировка консоли (иначе кракозябры).
   Ротация буферов: за один кадр конвертируется несколько строк. */
static char ru_buf[8][160];
static int  ru_i = 0;
static const char *ru(const char *utf8)
{
    wchar_t w[160];
    char *out = ru_buf[ru_i % 8];
    ru_i++;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w, 160);
    if (wlen <= 0) { out[0] = 0; return out; }
    int clen = WideCharToMultiByte(GetConsoleOutputCP(), 0, w, wlen - 1, out, 160, NULL, NULL);
    if (clen < 0) clen = 0;
    out[clen] = 0;
    return out;
}

/* ---------------- Настройки игры ---------------- */
#define WIN_SCORE     11   /* классика: первый до 11 */
#define PADDLE_H      4    /* высота ракетки в строках */
#define FRAME_MS      16   /* ~60 кадров в секунду */
#define BALL_START    0.45 /* стартовая скорость мяча (клеток/кадр) */
#define BALL_MAX      1.35 /* потолок скорости */
#define AI_MAX_SPEED  0.62 /* скорость ИИ (меньше 1, чтобы его можно было обыграть) */

/* ---------------- Состояние игры ---------------- */
typedef enum { ST_MENU, ST_PLAY, ST_OVER } State;

static State state = ST_MENU;
static int  mode_1p = 1;          /* 1 = против ИИ, 0 = вдвоём */
static int  score_l = 0, score_r = 0;

static float bx, by;              /* позиция мяча (float, рисуем округлённое) */
static float vx, vy;              /* скорость мяча */
static int   serve_to_right = 1;  /* куда летит подача */

static int pad_l_y, pad_r_y;      /* верх ракеток */
static int pad_l_dir = 0;         /* движение ракетки в кадре (для подкрутки) */
static int pad_r_dir = 0;

static int ai_target = 10;        /* куда целится ИИ */
static int ai_jitter = 0;         /* «человеческая» ошибка ИИ */
static int ai_timer = 0;

static int rows = 24, cols = 80;  /* размер терминала */

/* ---------------------------------------------------------------- */
static void reset_ball(void)
{
    bx = (float)cols / 2.0f;
    by = (float)rows / 2.0f;
    vx = BALL_START * (serve_to_right ? 1.0f : -1.0f);
    vy = ((rand() % 100) / 100.0f - 0.5f) * 0.6f; /* лёгкий случайный угол */
}

static void reset_game(void)
{
    score_l = score_r = 0;
    pad_l_y = rows / 2 - PADDLE_H / 2;
    pad_r_y = rows / 2 - PADDLE_H / 2;
    serve_to_right = (rand() % 2);
    reset_ball();
}

/* Подкрутка мяча ракеткой: точка удара + движение ракетки меняют vy */
static void paddle_bounce(float hit_offset, int paddle_dir, int is_right)
{
    vy += hit_offset * 0.9f + paddle_dir * 0.25f;
    if (vy >  1.2f) vy =  1.2f;
    if (vy < -1.2f) vy = -1.2f;

    vx = -vx * 1.04f;               /* каждый удар чуть ускоряет мяч */
    if (vx >  BALL_MAX) vx =  BALL_MAX;
    if (vx < -BALL_MAX) vx = -BALL_MAX;

    /* выталкиваем мяч из ракетки, чтобы не залипал */
    if (is_right) bx = (float)(cols - 3);
    else          bx = 2.0f;
    beep();
}

static void score_point(int right_scored)
{
    if (right_scored) score_r++; else score_l++;
    beep();

    if (score_l >= WIN_SCORE || score_r >= WIN_SCORE) {
        state = ST_OVER;
        return;
    }
    serve_to_right = right_scored; /* подача в сторону пропустившего */
    reset_ball();
    napms(400);                    /* секундная пауза после очка */
}

/* ИИ: следит за мячом с небольшой ошибкой, двигается с ограниченной скоростью */
static void ai_move(void)
{
    if (--ai_timer <= 0) {         /* раз в ~0.3с ИИ «переоценивает» мяч */
        ai_timer = 18;
        ai_jitter = rand() % 3 - 1;
    }

    float target = (float)rows / 2.0f;
    if (vx > 0)                    /* мяч летит к ИИ — едем к точке удара */
        target = by + ai_jitter;

    float center = pad_r_y + PADDLE_H / 2.0f;
    if (target > center + 0.4f) {
        if (pad_r_y + PADDLE_H < rows - 1) pad_r_y++;
        pad_r_dir = 1;
    } else if (target < center - 0.4f) {
        if (pad_r_y > 1) pad_r_y--;
        pad_r_dir = -1;
    } else {
        pad_r_dir = 0;
    }
}

static void update(void)
{
    /* --- ввод (выгребаем все нажатия за кадр) --- */
    pad_l_dir = pad_r_dir = 0;
    int ch;
    while ((ch = getch()) != ERR) {
        if (ch == 'q' || ch == 'Q') { endwin(); exit(0); }
        if (ch == 'm' || ch == 'M') { state = ST_MENU; return; }
        if (state != ST_PLAY) {
            if (state == ST_OVER && (ch == 'r' || ch == 'R')) reset_game(), state = ST_PLAY;
            if (state == ST_MENU && ch == '1') { mode_1p = 1; reset_game(); state = ST_PLAY; }
            if (state == ST_MENU && ch == '2') { mode_1p = 0; reset_game(); state = ST_PLAY; }
            continue;
        }
        switch (ch) {
        case 'w': case 'W':
            if (pad_l_y > 1) { pad_l_y--; pad_l_dir = -1; }
            break;
        case 's': case 'S':
            if (pad_l_y + PADDLE_H < rows - 1) { pad_l_y++; pad_l_dir = 1; }
            break;
        case KEY_UP:
            if (!mode_1p && pad_r_y > 1) { pad_r_y--; pad_r_dir = -1; }
            break;
        case KEY_DOWN:
            if (!mode_1p && pad_r_y + PADDLE_H < rows - 1) { pad_r_y++; pad_r_dir = 1; }
            break;
        }
    }

    if (state != ST_PLAY)
        return;

    /* --- ИИ --- */
    if (mode_1p)
        ai_move();

    /* --- мяч --- */
    bx += vx;
    by += vy;

    /* отскок от верха и низа */
    if (by < 1)           { by = 1;           vy = -vy; beep(); }
    if (by > rows - 2)    { by = rows - 2;    vy = -vy; beep(); }

    /* левая ракетка: колонки 2 */
    if (vx < 0 && bx <= 3.0f && bx >= 1.0f) {
        if ((int)(by + 0.5f) >= pad_l_y && (int)(by + 0.5f) < pad_l_y + PADDLE_H) {
            float hit = ((by + 0.5f) - (pad_l_y + PADDLE_H / 2.0f)) / (PADDLE_H / 2.0f);
            paddle_bounce(hit, pad_l_dir, 0);
        }
    }
    /* правая ракетка: колонки cols-3 */
    if (vx > 0 && bx >= (float)cols - 3.0f && bx <= (float)(cols - 2)) {
        if ((int)(by + 0.5f) >= pad_r_y && (int)(by + 0.5f) < pad_r_y + PADDLE_H) {
            float hit = ((by + 0.5f) - (pad_r_y + PADDLE_H / 2.0f)) / (PADDLE_H / 2.0f);
            paddle_bounce(hit, pad_r_dir, 1);
        }
    }

    /* пропустили — очко */
    if (bx < 0)          score_point(1);
    else if (bx > cols)  score_point(0);
}

/* ---------------------------------------------------------------- */
static void draw_scene(void)
{
    int y, i;
    erase();

    /* пунктирная центральная линия */
    for (y = 1; y < rows - 1; y += 2)
        mvaddch(y, cols / 2, ACS_VLINE);

    /* счёт сверху */
    attron(A_BOLD);
    mvprintw(0, cols / 2 - 4, "%d   %d", score_l, score_r);
    attroff(A_BOLD);

    /* ракетки */
    for (i = 0; i < PADDLE_H; i++) {
        mvaddch(pad_l_y + i, 2, '|');
        mvaddch(pad_r_y + i, cols - 3, '|');
    }

    /* мяч */
    mvaddch((int)(by + 0.5f), (int)(bx + 0.5f), '@');

    /* подсказка снизу */
    attron(A_DIM);
    if (mode_1p)
        mvprintw(rows - 1, 1, " W/S - %s  |  M %s  Q %s  |  %s: %s",
                 ru("ты"), ru("меню"), ru("выход"), ru("соперник"), ru("ИИ"));
    else
        mvprintw(rows - 1, 1, " W/S %s ^/v  |  M %s  Q %s",
                 ru("и"), ru("меню"), ru("выход"));
    attroff(A_DIM);

    refresh();
}

static void draw_menu(void)
{
    erase();
    attron(A_BOLD);
    mvprintw(rows / 2 - 6, cols / 2 - 12, "  ####   ####  #    #");
    mvprintw(rows / 2 - 5, cols / 2 - 12, " #    # #    # #   # ");
    mvprintw(rows / 2 - 4, cols / 2 - 12, " #    # #    # ####  ");
    mvprintw(rows / 2 - 3, cols / 2 - 12, " #    # #    # #  #  ");
    mvprintw(rows / 2 - 2, cols / 2 - 12, "  ####   ####  #   # ");
    attroff(A_BOLD);

    mvprintw(rows / 2 + 1, cols / 2 - 12, "[1] %s", ru("Играть против ИИ"));
    mvprintw(rows / 2 + 2, cols / 2 - 12, "[2] %s", ru("Играть вдвоём"));
    mvprintw(rows / 2 + 4, cols / 2 - 12, "%s %d %s", ru("Игра до"), WIN_SCORE, ru("очков. Удачи!"));
    mvprintw(rows / 2 + 6, cols / 2 - 12, "Q - %s", ru("выход"));

    refresh();
}

static void draw_over(void)
{
    erase();
    attron(A_BOLD);
    if (score_l > score_r)
        mvprintw(rows / 2 - 3, cols / 2 - 8, "%s!", ru("ПОБЕДА ЛЕВОГО"));
    else
        mvprintw(rows / 2 - 3, cols / 2 - 8, "%s!", ru("ПОБЕДА ПРАВОГО"));
    attroff(A_BOLD);
    mvprintw(rows / 2 - 1, cols / 2 - 4, "%d : %d", score_l, score_r);
    mvprintw(rows / 2 + 2, cols / 2 - 16, "[R] %s   [M] %s   [Q] %s",
             ru("реванш"), ru("меню"), ru("выход"));
    refresh();
}

/* ---------------------------------------------------------------- */
int main(void)
{
    initscr();
    cbreak();
    noecho();
    keypad(stdscr, TRUE);
    nodelay(stdscr, TRUE);
    curs_set(0);

    if (has_colors()) {
        start_color();
        init_pair(1, COLOR_GREEN,  COLOR_BLACK);
        init_pair(2, COLOR_YELLOW, COLOR_BLACK);
        attron(COLOR_PAIR(1));
    }

    srand((unsigned)time(NULL));
    getmaxyx(stdscr, rows, cols);

    if (cols < 50 || rows < 18) {
        endwin();
        fprintf(stderr, "%s\n", ru("Терминал слишком маленький: нужно минимум 50x18."));
        return 1;
    }

    reset_game();

    while (1) {
        getmaxyx(stdscr, rows, cols);

        switch (state) {
        case ST_MENU:  draw_menu();  break;
        case ST_PLAY:  draw_scene(); break;
        case ST_OVER:  draw_over();  break;
        }
        update();
        napms(FRAME_MS);
    }

    endwin();
    return 0;
}
