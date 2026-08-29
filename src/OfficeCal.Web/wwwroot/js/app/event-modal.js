// 事件建立／編輯彈窗 + 明細彈窗。以 Bootstrap Modal 呈現，父元件透過 ref 呼叫 open*()。
(function () {
  window.EventModal = {
    components: { RecurrenceEditor: window.RecurrenceEditor },
    props: { rooms: { type: Array, default: () => [] }, currentUserId: { type: Number, required: true } },
    emits: ['saved'],
    data() {
      return {
        modal: null,
        mode: 'create',        // create | edit
        editScope: 'series',   // series | single
        saving: false,
        eventId: null,
        occurrenceId: null,
        canEdit: true,
        isRecurring: false,
        users: [],
        attendeeWarnings: [],
        form: this.blank(),
        // 掛在 <recurrence-editor :key="editorKey"> 上。每次開彈窗（openCreate／openEdit）
        // 遞增一次，強制 Vue 銷毀並重新掛載該子元件，把它內部的區域狀態
        // （例如「使用者是否手動要求每月最後一個」的意圖旗標）歸零，避免跨事件殘留。
        // 只在開彈窗時遞增，不要在彈窗內的其他互動（如模式切換）時動它。
        editorKey: 0,
      };
    },
    mounted() {
      this.modal = new bootstrap.Modal(this.$refs.root);
      window.api.get('/api/v1/users/picker').then((u) => { this.users = u; });
    },
    computed: {
      startDate() { return this.form.startAt.slice(0, 10); },
      // 不要在同一個元素上同時寫 v-for 與 v-if：Vue 3 的 v-if 優先度較高，
      // 會在迴圈變數還不存在時求值。先在這裡濾好。
      selectableUsers() { return this.users.filter((u) => u.id !== this.currentUserId); },
      title() {
        if (this.mode === 'create') return '建立事件';
        return this.editScope === 'single' ? '編輯這一筆' : '編輯整個系列';
      },
      singleLocked() { return this.mode === 'edit' && this.editScope === 'single'; },
    },
    methods: {
      blank() {
        const now = new Date();
        now.setMinutes(0, 0, 0);
        const start = new Date(now.getTime() + 3600000);
        return {
          title: '', description: '', roomId: null,
          startAt: window.api.toLocalIso(start).slice(0, 16),
          endAt: window.api.toLocalIso(new Date(start.getTime() + 3600000)).slice(0, 16),
          isAllDay: false, attendeeIds: [], recurrence: null,
        };
      },

      /** 從行事曆空白格快速建立。start/end 為 Date，roomId 可選。 */
      openCreate(start, end, roomId) {
        this.mode = 'create';
        this.editScope = 'series';
        this.eventId = null;
        this.occurrenceId = null;
        this.canEdit = true;
        this.isRecurring = false;
        this.attendeeWarnings = [];
        this.editorKey++;
        this.form = this.blank();
        if (start) this.form.startAt = window.api.toLocalIso(start).slice(0, 16);
        if (end) this.form.endAt = window.api.toLocalIso(end).slice(0, 16);
        if (roomId) this.form.roomId = roomId;
        this.modal.show();
      },

      /** 從既有 occurrence 開啟編輯。scope = 'single' | 'series'。 */
      async openEdit(occurrence, scope) {
        const detail = await window.api.get('/api/v1/events/' + occurrence.eventId);
        this.mode = 'edit';
        this.editScope = scope;
        this.eventId = detail.id;
        this.occurrenceId = occurrence.occurrenceId;
        this.canEdit = detail.canEdit;
        this.isRecurring = !!detail.recurrence;
        this.attendeeWarnings = [];
        this.editorKey++;

        this.form = {
          title: scope === 'single' ? occurrence.title : detail.title,
          description: detail.description || '',
          roomId: detail.roomId,
          startAt: (scope === 'single' ? occurrence.startAt : detail.startAt).slice(0, 16),
          endAt: (scope === 'single' ? occurrence.endAt : detail.endAt).slice(0, 16),
          isAllDay: detail.isAllDay,
          attendeeIds: detail.attendees.map((a) => a.userId),
          recurrence: scope === 'single' ? null : detail.recurrence,
        };
        this.modal.show();
      },

      async checkAttendees() {
        if (this.form.attendeeIds.length === 0) { this.attendeeWarnings = []; return; }
        const result = await window.api.post('/api/v1/events/check-attendees', {
          attendeeIds: this.form.attendeeIds,
          slots: [{ startAt: this.form.startAt + ':00', endAt: this.form.endAt + ':00' }],
        });
        this.attendeeWarnings = result.filter((r) => r.conflictCount > 0);
      },

      async save() {
        if (!this.form.title.trim()) {
          Swal.fire('請輸入標題', '', 'info');
          return;
        }
        const body = {
          title: this.form.title.trim(),
          description: this.form.description,
          roomId: this.form.roomId || null,
          startAt: this.form.startAt + ':00',
          endAt: this.form.endAt + ':00',
          isAllDay: this.form.isAllDay,
          attendeeIds: this.form.attendeeIds,
          recurrence: this.form.recurrence,
          occurrenceId: this.editScope === 'single' ? this.occurrenceId : null,
        };

        this.saving = true;
        try {
          if (this.mode === 'create') {
            await window.api.post('/api/v1/events', body);
          } else {
            await window.api.put(
              `/api/v1/events/${this.eventId}?mode=${this.editScope}`, body);
          }
          this.modal.hide();
          this.$emit('saved');
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 409 的衝突明細已由攔截器以 SweetAlert2 呈現，彈窗保持開啟讓使用者改時段
        } finally {
          this.saving = false;
        }
      },
    },
    template: `
<div class="modal fade" tabindex="-1" ref="root">
  <div class="modal-dialog modal-lg modal-dialog-scrollable">
    <div class="modal-content">
      <div class="modal-header">
        <h5 class="modal-title">{{ title }}</h5>
        <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
      </div>
      <div class="modal-body">
        <div class="mb-3">
          <label class="form-label">標題</label>
          <input class="form-control" v-model="form.title" maxlength="100" />
        </div>

        <div class="mb-3" v-if="!singleLocked">
          <label class="form-label">說明</label>
          <textarea class="form-control" rows="2" v-model="form.description"
                    maxlength="1000"></textarea>
        </div>

        <div class="row g-3 mb-3">
          <div class="col-md-6">
            <label class="form-label">開始</label>
            <input type="datetime-local" class="form-control" v-model="form.startAt"
                   @change="checkAttendees" />
          </div>
          <div class="col-md-6">
            <label class="form-label">結束</label>
            <input type="datetime-local" class="form-control" v-model="form.endAt"
                   @change="checkAttendees" />
          </div>
        </div>

        <div class="form-check mb-3" v-if="!singleLocked">
          <input class="form-check-input" type="checkbox" id="all-day" v-model="form.isAllDay" />
          <label class="form-check-label" for="all-day">全天事件（00:00–23:59）</label>
        </div>

        <div class="mb-3">
          <label class="form-label">會議廳</label>
          <select class="form-select" v-model="form.roomId" :disabled="singleLocked">
            <option :value="null">不指定（純個人事件，不占用資源）</option>
            <option v-for="r in rooms" :key="r.id" :value="r.id">
              {{ r.name }}（{{ r.capacity }} 人）{{ r.location ? '・' + r.location : '' }}
            </option>
          </select>
          <div class="form-text" v-if="singleLocked">
            單筆編輯不可變更會議廳。要換會議廳請取消這一筆後另建事件。
          </div>
        </div>

        <div class="mb-3" v-if="!singleLocked">
          <label class="form-label">與會者</label>
          <select class="form-select" multiple size="6" v-model="form.attendeeIds"
                  @change="checkAttendees">
            <option v-for="u in selectableUsers" :key="u.id" :value="u.id">
              {{ u.displayName }}（{{ u.employeeNo }}）{{ u.departmentName ? '・' + u.departmentName : '' }}
            </option>
          </select>
          <div class="form-text">按住 Ctrl／Cmd 可複選。</div>
          <div class="alert alert-warning py-2 px-3 mt-2 mb-0" v-if="attendeeWarnings.length">
            <div class="small" v-for="w in attendeeWarnings" :key="w.userId">
              {{ w.displayName }} 該時段已有 {{ w.conflictCount }} 場會議（{{ w.titles.join('、') }}）
            </div>
            <div class="small text-muted mt-1">這只是提示，仍可直接送出。</div>
          </div>
        </div>

        <div class="mb-2" v-if="!singleLocked">
          <recurrence-editor :key="editorKey" v-model="form.recurrence"
                              :start-date="startDate"></recurrence-editor>
        </div>
        <div class="alert alert-info py-2 px-3 small mb-0" v-if="singleLocked">
          只會修改這一次發生，其餘各次不受影響。
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
        <button class="btn btn-primary" :disabled="saving || !canEdit" @click="save">
          {{ saving ? '儲存中…' : '儲存' }}
        </button>
      </div>
    </div>
  </div>
</div>`,
  };
})();
