// 事件明細彈窗：完整資訊、與會者名單、「這一筆／整個系列」的編輯與取消、單筆 .ics 下載。
(function () {
  window.EventDetailModal = {
    emits: ['edit', 'changed'],
    data() { return { modal: null, occ: null, detail: null }; },
    mounted() { this.modal = new bootstrap.Modal(this.$refs.root); },
    computed: {
      isRecurring() { return !!(this.detail && this.detail.recurrence); },
      when() {
        if (!this.occ) return '';
        return window.api.fmtDateTime(this.occ.startAt) + ' – '
             + window.api.fmtDateTime(this.occ.endAt).split(' ')[1];
      },
    },
    methods: {
      async open(occurrence) {
        this.occ = occurrence;
        this.detail = await window.api.get('/api/v1/events/' + occurrence.eventId);
        this.modal.show();
      },
      edit(scope) {
        this.modal.hide();
        this.$emit('edit', { occurrence: this.occ, scope: scope });
      },
      async cancel(scope) {
        const text = scope === 'single'
          ? '只會取消這一次發生，該時段的會議廳會被釋出。'
          : '整個系列的所有次數都會被取消。';
        const confirmed = await Swal.fire({
          icon: 'warning', title: '確定要取消嗎？', text: text,
          showCancelButton: true, confirmButtonText: '確定取消', cancelButtonText: '再想想',
        });
        if (!confirmed.isConfirmed) return;

        let url = `/api/v1/events/${this.detail.id}?mode=${scope}`;
        if (scope === 'single') url += `&occurrenceId=${this.occ.occurrenceId}`;
        await window.api.del(url);

        this.modal.hide();
        this.$emit('changed');
        Swal.fire({ icon: 'success', title: '已取消', timer: 1200, showConfirmButton: false });
      },
      downloadIcs() { window.location.href = `/api/v1/events/${this.detail.id}/ics`; },
    },
    template: `
<div class="modal fade" tabindex="-1" ref="root">
  <div class="modal-dialog modal-dialog-centered">
    <div class="modal-content" v-if="detail">
      <div class="modal-header">
        <h5 class="modal-title">{{ occ.title }}</h5>
        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
      </div>
      <div class="modal-body">
        <dl class="row mb-0 small">
          <dt class="col-4">時間</dt><dd class="col-8">{{ when }}</dd>
          <dt class="col-4">會議廳</dt>
          <dd class="col-8">{{ detail.roomName || '未指定（純個人事件）' }}</dd>
          <dt class="col-4">預約人</dt><dd class="col-8">{{ detail.ownerName }}</dd>
          <dt class="col-4" v-if="isRecurring">重複</dt>
          <dd class="col-8" v-if="isRecurring">此事件屬於一個重複系列</dd>
          <dt class="col-4" v-if="detail.description">說明</dt>
          <dd class="col-8" v-if="detail.description" style="white-space:pre-wrap">
            {{ detail.description }}
          </dd>
          <dt class="col-4">與會者</dt>
          <dd class="col-8">
            <span v-if="!detail.attendees.length" class="text-muted">無</span>
            <span v-for="a in detail.attendees" :key="a.userId"
                  class="badge text-bg-light me-1 mb-1">{{ a.displayName }}</span>
          </dd>
        </dl>
      </div>
      <div class="modal-footer justify-content-between">
        <button class="btn btn-outline-secondary btn-sm" @click="downloadIcs">下載 .ics</button>
        <div v-if="detail.canEdit">
          <template v-if="isRecurring">
            <button class="btn btn-outline-primary btn-sm me-1" @click="edit('single')">
              編輯這一筆
            </button>
            <button class="btn btn-outline-primary btn-sm me-3" @click="edit('series')">
              編輯整個系列
            </button>
            <button class="btn btn-outline-danger btn-sm me-1" @click="cancel('single')">
              取消這一筆
            </button>
            <button class="btn btn-outline-danger btn-sm" @click="cancel('series')">
              取消整個系列
            </button>
          </template>
          <template v-else>
            <button class="btn btn-outline-primary btn-sm me-1" @click="edit('series')">編輯</button>
            <button class="btn btn-outline-danger btn-sm" @click="cancel('series')">取消預約</button>
          </template>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
