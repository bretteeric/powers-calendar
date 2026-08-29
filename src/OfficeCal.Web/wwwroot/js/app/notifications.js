// 導覽列的通知中心：未讀紅點 + 下拉清單，點擊跳至該事件所在的日檢視。
(function () {
  window.NotificationCenter = {
    data() { return { items: [], unread: 0, open: false, loading: false }; },
    mounted() {
      this.load();
      // 每 60 秒輪詢一次未讀數；規格不做推播，輪詢已足夠。
      setInterval(this.load, 60000);
      document.addEventListener('click', this.onDocumentClick);
    },
    beforeUnmount() { document.removeEventListener('click', this.onDocumentClick); },
    methods: {
      onDocumentClick(e) { if (!this.$el.contains(e.target)) this.open = false; },
      async load() {
        const data = await window.api.get('/api/v1/notifications', { params: { take: 20 } });
        this.items = data.items;
        this.unread = data.unreadCount;
      },
      fmt(iso) { return window.api.fmtDateTime(iso); },
      async click(n) {
        if (!n.isRead) {
          await window.api.post(`/api/v1/notifications/${n.id}/read`);
          n.isRead = true;
          this.unread = Math.max(0, this.unread - 1);
        }
        if (!n.eventId) return;
        try {
          const detail = await window.api.get('/api/v1/events/' + n.eventId);
          const date = detail.startAt.slice(0, 10);
          window.location.href = `/?view=day&date=${date}`;
        } catch (e) {
          window.location.href = '/';   // 事件已被刪除或沒有權限，回行事曆
        }
      },
    },
    template: `
<div class="position-relative">
  <button class="btn btn-link text-decoration-none position-relative p-1"
          @click.stop="open = !open" title="通知">
    <span style="font-size:1.25rem">🔔</span>
    <span v-if="unread > 0"
          class="position-absolute top-0 start-100 translate-middle badge rounded-pill text-bg-danger">
      {{ unread > 99 ? '99+' : unread }}
    </span>
  </button>
  <div v-show="open" class="card card-oc shadow position-absolute end-0 mt-1"
       style="width:340px; max-height:420px; overflow:auto; z-index:1050">
    <div class="p-2 border-bottom small fw-semibold">通知</div>
    <div v-if="!items.length" class="p-3 text-muted small text-center">目前沒有通知</div>
    <a v-for="n in items" :key="n.id" href="#"
       class="d-block px-3 py-2 border-bottom text-decoration-none"
       :class="n.isRead ? 'text-muted' : 'fw-semibold text-dark'"
       @click.prevent="click(n)">
      <div class="small">{{ n.message }}</div>
      <div style="font-size:.72rem" class="text-muted">{{ fmt(n.createdAt) }}</div>
    </a>
  </div>
</div>`,
  };
})();
