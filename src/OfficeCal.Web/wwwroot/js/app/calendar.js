// 我的行事曆：月／週／日三種檢視。資料一律讀 occurrence（scope=me）。
(function () {
  const HOUR_H = 44;   // 與 site.css 的 .oc-hour-row 高度一致
  const DAY_NAMES = ['日', '一', '二', '三', '四', '五', '六'];

  function startOfDay(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
  function addDays(d, n) { const x = new Date(d); x.setDate(x.getDate() + n); return x; }
  function startOfWeek(d) { return addDays(startOfDay(d), -d.getDay()); }
  function sameDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth()
        && a.getDate() === b.getDate();
  }

  window.CalendarPage = {
    components: { EventModal: window.EventModal, EventDetailModal: window.EventDetailModal },
    props: { currentUserId: { type: Number, required: true } },
    data() {
      const params = new URLSearchParams(window.location.search);
      return {
        view: params.get('view') || 'month',
        anchor: params.get('date') ? window.api.parseDate(params.get('date')) : new Date(),
        items: [],
        rooms: [],
        hours: Array.from({ length: 24 }, (_, i) => i),
        dayNames: DAY_NAMES,
        loading: false,
      };
    },
    computed: {
      rangeStart() {
        if (this.view === 'month') return startOfWeek(new Date(this.anchor.getFullYear(),
                                                              this.anchor.getMonth(), 1));
        if (this.view === 'week') return startOfWeek(this.anchor);
        return startOfDay(this.anchor);
      },
      rangeEnd() {
        if (this.view === 'month') return addDays(this.rangeStart, 42);
        if (this.view === 'week') return addDays(this.rangeStart, 7);
        return addDays(this.rangeStart, 1);
      },
      heading() {
        const d = this.anchor;
        if (this.view === 'month') return `${d.getFullYear()} 年 ${d.getMonth() + 1} 月`;
        if (this.view === 'week') {
          const s = this.rangeStart, e = addDays(this.rangeStart, 6);
          return `${s.getFullYear()}/${s.getMonth() + 1}/${s.getDate()}`
               + ` – ${e.getMonth() + 1}/${e.getDate()}`;
        }
        return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}（${DAY_NAMES[d.getDay()]}）`;
      },
      monthCells() {
        return Array.from({ length: 42 }, (_, i) => addDays(this.rangeStart, i));
      },
      weekCells() {
        const n = this.view === 'week' ? 7 : 1;
        return Array.from({ length: n }, (_, i) => addDays(this.rangeStart, i));
      },
    },
    mounted() {
      window.api.get('/api/v1/rooms').then((r) => { this.rooms = r; });
      this.load();
    },
    methods: {
      async load() {
        this.loading = true;
        try {
          this.items = await window.api.get('/api/v1/events', {
            params: {
              from: window.api.toLocalIso(this.rangeStart),
              to: window.api.toLocalIso(this.rangeEnd),
              scope: 'me',
            },
          });
        } finally {
          this.loading = false;
        }
      },
      setView(v) { this.view = v; this.load(); },
      move(step) {
        const d = new Date(this.anchor);
        if (this.view === 'month') d.setMonth(d.getMonth() + step);
        else if (this.view === 'week') d.setDate(d.getDate() + step * 7);
        else d.setDate(d.getDate() + step);
        this.anchor = d;
        this.load();
      },
      goToday() { this.anchor = new Date(); this.load(); },

      isToday(d) { return sameDay(d, new Date()); },
      isOtherMonth(d) { return d.getMonth() !== this.anchor.getMonth(); },
      itemsOn(day) {
        return this.items.filter((o) => sameDay(new Date(o.startAt), day))
                         .sort((a, b) => a.startAt.localeCompare(b.startAt));
      },
      timeLabel(iso) { return window.api.fmtDateTime(iso).split(' ')[1]; },

      slotStyle(o) {
        const s = new Date(o.startAt), e = new Date(o.endAt);
        const top = (s.getHours() + s.getMinutes() / 60) * HOUR_H;
        const height = Math.max(18, ((e - s) / 3600000) * HOUR_H - 2);
        return { top: top + 'px', height: height + 'px' };
      },

      /** 點空白格快速建立：月檢視給整點 09:00，週／日檢視依點擊位置取整點。 */
      quickCreate(day, hour) {
        const start = new Date(day.getFullYear(), day.getMonth(), day.getDate(),
                               hour == null ? 9 : hour, 0, 0);
        this.$refs.editor.openCreate(start, new Date(start.getTime() + 3600000), null);
      },
      openDetail(o) { this.$refs.detail.open(o); },
      onEdit(payload) { this.$refs.editor.openEdit(payload.occurrence, payload.scope); },
    },
    template: `
<div>
  <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
    <div class="btn-group">
      <button class="btn btn-outline-secondary btn-sm" @click="move(-1)">‹</button>
      <button class="btn btn-outline-secondary btn-sm" @click="goToday">今天</button>
      <button class="btn btn-outline-secondary btn-sm" @click="move(1)">›</button>
    </div>
    <h1 class="h5 mb-0 ms-2">{{ heading }}</h1>
    <div class="ms-auto btn-group">
      <button class="btn btn-sm" :class="view==='month' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('month')">月</button>
      <button class="btn btn-sm" :class="view==='week' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('week')">週</button>
      <button class="btn btn-sm" :class="view==='day' ? 'btn-primary':'btn-outline-primary'"
              @click="setView('day')">日</button>
    </div>
    <button class="btn btn-primary btn-sm" @click="quickCreate(anchor, null)">＋ 新增事件</button>
  </div>

  <div class="card-oc p-0 overflow-hidden">
    <!-- 月檢視 -->
    <template v-if="view === 'month'">
      <div class="d-grid" style="grid-template-columns: repeat(7,1fr)">
        <div v-for="n in dayNames" :key="n" class="text-center small text-muted py-2">{{ n }}</div>
      </div>
      <div class="oc-month">
        <div v-for="d in monthCells" :key="d.toISOString()"
             class="oc-month-cell"
             :class="{ 'is-other-month': isOtherMonth(d), 'is-today': isToday(d) }"
             @click="quickCreate(d, null)">
          <div class="small fw-semibold mb-1">{{ d.getDate() }}</div>
          <span v-for="o in itemsOn(d)" :key="o.occurrenceId"
                class="oc-chip" :class="{ 'is-room': o.roomId }"
                :title="o.title"
                @click.stop="openDetail(o)">
            {{ o.isAllDay ? '全天' : timeLabel(o.startAt) }} {{ o.title }}
          </span>
        </div>
      </div>
    </template>

    <!-- 週／日檢視 -->
    <template v-else>
      <div class="d-flex border-bottom">
        <div style="width:56px"></div>
        <div v-for="d in weekCells" :key="d.toISOString()"
             class="flex-fill text-center small py-2"
             :class="{ 'fw-bold text-primary': isToday(d) }">
          {{ d.getMonth() + 1 }}/{{ d.getDate() }}（{{ dayNames[d.getDay()] }}）
        </div>
      </div>
      <div class="d-flex" style="max-height:620px; overflow-y:auto">
        <div style="width:56px">
          <div v-for="h in hours" :key="h" class="oc-hour-row text-end pe-2 small text-muted">
            {{ String(h).padStart(2,'0') }}:00
          </div>
        </div>
        <div v-for="d in weekCells" :key="d.toISOString()" class="flex-fill oc-grid">
          <div v-for="h in hours" :key="h" class="oc-hour-row" @click="quickCreate(d, h)"></div>
          <div v-for="o in itemsOn(d)" :key="o.occurrenceId"
               class="oc-slot" :style="slotStyle(o)" @click.stop="openDetail(o)">
            <div class="fw-semibold">{{ o.title }}</div>
            <div class="opacity-75">{{ o.roomName || '個人事件' }}</div>
          </div>
        </div>
      </div>
    </template>
  </div>

  <div class="text-muted small mt-2" v-if="loading">載入中…</div>

  <event-modal ref="editor" :rooms="rooms" :current-user-id="currentUserId"
               @saved="load"></event-modal>
  <event-detail-modal ref="detail" @edit="onEdit" @changed="load"></event-detail-modal>
</div>`,
  };
})();
