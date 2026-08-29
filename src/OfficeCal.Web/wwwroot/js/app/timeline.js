// 會議廳資源時間軸：橫軸時間、縱軸會議廳，一眼看出空檔。
(function () {
  const DAY_MINUTES = 24 * 60;

  window.TimelinePage = {
    components: { EventModal: window.EventModal, EventDetailModal: window.EventDetailModal },
    props: { currentUserId: { type: Number, required: true } },
    data() {
      const today = new Date();
      return {
        date: window.api.toLocalIso(today).slice(0, 10),
        capacity: null,
        rows: [],
        rooms: [],
        hours: Array.from({ length: 24 }, (_, i) => i),
        loading: false,
      };
    },
    mounted() {
      window.api.get('/api/v1/rooms').then((r) => { this.rooms = r; });
      this.load();
    },
    methods: {
      async load() {
        this.loading = true;
        try {
          this.rows = await window.api.get('/api/v1/rooms/availability', {
            params: { date: this.date, capacity: this.capacity || null },
          });
        } finally {
          this.loading = false;
        }
      },
      shiftDay(step) {
        const d = window.api.parseDate(this.date);
        d.setDate(d.getDate() + step);
        this.date = window.api.toLocalIso(d).slice(0, 10);
        this.load();
      },
      /**
       * iso 的時刻換算成「以目前檢視日期 0 時為基準」的分鐘數。跨日事件會因此
       * 落在 [0, DAY_MINUTES) 之外（負值＝早於當日、超過 DAY_MINUTES＝晚於當日），
       * 讓 slotStyle 既有的 clamp 能真正發揮作用，而不是把跨日事件誤畫成當日內的
       * 極小色塊。純比較日期（不含時分），避免時區位移造成的誤差。
       */
      minutesOf(iso) {
        const d = new Date(iso);
        const view = window.api.parseDate(this.date);
        const occDay = new Date(d.getFullYear(), d.getMonth(), d.getDate());
        const dayDiff = Math.round((occDay - view) / 86400000);
        return dayDiff * DAY_MINUTES + d.getHours() * 60 + d.getMinutes();
      },
      slotStyle(b) {
        const start = Math.max(0, this.minutesOf(b.startAt));
        const end = Math.min(DAY_MINUTES, this.minutesOf(b.endAt) || DAY_MINUTES);
        return {
          left: (start / DAY_MINUTES * 100) + '%',
          width: (Math.max(15, end - start) / DAY_MINUTES * 100) + '%',
        };
      },
      /** 點空白區塊：依點擊的水平位置換算成整點，帶入該會議廳開始預約。 */
      quickBook(row, ev) {
        const rect = ev.currentTarget.getBoundingClientRect();
        const ratio = Math.min(0.99, Math.max(0, (ev.clientX - rect.left) / rect.width));
        const hour = Math.floor(ratio * 24);
        const d = window.api.parseDate(this.date);
        const start = new Date(d.getFullYear(), d.getMonth(), d.getDate(), hour, 0, 0);
        this.$refs.editor.openCreate(start, new Date(start.getTime() + 3600000), row.roomId);
      },
      openDetail(b, row) {
        this.$refs.detail.open({
          occurrenceId: b.occurrenceId, eventId: b.eventId, title: b.title,
          startAt: b.startAt, endAt: b.endAt, roomId: row.roomId, roomName: row.name,
        });
      },
      onEdit(payload) { this.$refs.editor.openEdit(payload.occurrence, payload.scope); },
    },
    template: `
<div>
  <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
    <div class="btn-group">
      <button class="btn btn-outline-secondary btn-sm" @click="shiftDay(-1)">‹</button>
      <button class="btn btn-outline-secondary btn-sm" @click="shiftDay(1)">›</button>
    </div>
    <input type="date" class="form-control form-control-sm" style="width:auto"
           v-model="date" @change="load" />
    <div class="input-group input-group-sm" style="width:210px">
      <span class="input-group-text">最少可容納</span>
      <input type="number" min="1" class="form-control" v-model.number="capacity"
             @change="load" placeholder="不限" />
      <span class="input-group-text">人</span>
    </div>
    <span class="text-muted small ms-2" v-if="loading">載入中…</span>
  </div>

  <div class="card-oc p-3">
    <div class="d-flex">
      <div style="width:190px"></div>
      <div class="flex-fill oc-timeline-head">
        <div v-for="h in hours" :key="h" class="flex-fill">
          {{ h % 2 === 0 ? String(h).padStart(2,'0') : '' }}
        </div>
      </div>
    </div>

    <div v-for="row in rows" :key="row.roomId" class="d-flex align-items-stretch">
      <div style="width:190px" class="pe-2 py-2 border-end">
        <div class="fw-semibold small">{{ row.name }}</div>
        <div class="text-muted" style="font-size:.72rem">
          {{ row.capacity }} 人{{ row.location ? '・' + row.location : '' }}
        </div>
      </div>
      <div class="flex-fill oc-timeline-row" @click="quickBook(row, $event)"
           :title="'點一下即可預約 ' + row.name">
        <div v-for="b in row.busy" :key="b.occurrenceId"
             class="oc-timeline-slot" :style="slotStyle(b)"
             :title="b.title + '（' + b.ownerName + '）'"
             @click.stop="openDetail(b, row)">
          {{ b.title }}
        </div>
      </div>
    </div>

    <div class="text-muted small py-4 text-center" v-if="!rows.length && !loading">
      沒有符合條件的會議廳。
    </div>
  </div>

  <event-modal ref="editor" :rooms="rooms" :current-user-id="currentUserId"
               @saved="load"></event-modal>
  <event-detail-modal ref="detail" @edit="onEdit" @changed="load"></event-detail-modal>
</div>`,
  };
})();
