(function () {
  window.AdminRoomsPage = {
    data() {
      return { rooms: [], editing: null, saving: false, modal: null };
    },
    mounted() { this.modal = new bootstrap.Modal(this.$refs.dialog); this.load(); },
    methods: {
      async load() {
        this.rooms = await window.api.get('/api/v1/rooms', { params: { includeInactive: true } });
      },
      blank() {
        return { id: null, name: '', location: '', capacity: 10, equipment: '', isActive: true };
      },
      openCreate() { this.editing = this.blank(); this.modal.show(); },
      openEdit(r) { this.editing = Object.assign({}, r); this.modal.show(); },
      async save() {
        const body = {
          name: this.editing.name, location: this.editing.location,
          capacity: Number(this.editing.capacity), equipment: this.editing.equipment,
          isActive: this.editing.isActive,
        };
        this.saving = true;
        try {
          if (this.editing.id) await window.api.put('/api/v1/rooms/' + this.editing.id, body);
          else await window.api.post('/api/v1/rooms', body);
          this.modal.hide();
          await this.load();
          Swal.fire({ icon: 'success', title: '已儲存', timer: 1200, showConfirmButton: false });
        } catch (e) {
          // 攔截器已顯示訊息（例如名稱重複）
        } finally {
          this.saving = false;
        }
      },
      async toggleActive(r) {
        const next = !r.isActive;
        const ok = await Swal.fire({
          icon: 'question',
          title: next ? '要啟用這間會議廳嗎？' : '要停用這間會議廳嗎？',
          text: next ? '啟用後可以再被預約。' : '停用後不可新增預約，既有預約仍會保留。',
          showCancelButton: true, confirmButtonText: '確定', cancelButtonText: '取消',
        });
        if (!ok.isConfirmed) return;
        try {
          await window.api.put('/api/v1/rooms/' + r.id, {
            name: r.name, location: r.location, capacity: r.capacity,
            equipment: r.equipment, isActive: next,
          });
          await this.load();
        } catch (e) {
          // 攔截器已顯示訊息
        }
      },
    },
    template: `
<div>
  <div class="d-flex align-items-center mb-3">
    <h1 class="h5 mb-0">會議廳管理</h1>
    <button class="btn btn-primary btn-sm ms-auto" @click="openCreate">＋ 新增會議廳</button>
  </div>

  <div class="card-oc p-0">
    <table class="table table-hover align-middle mb-0">
      <thead class="table-light">
        <tr><th>名稱</th><th>位置</th><th class="text-end">容納人數</th><th>設備</th>
            <th>狀態</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-for="r in rooms" :key="r.id">
          <td class="fw-semibold">{{ r.name }}</td>
          <td class="text-muted small">{{ r.location }}</td>
          <td class="text-end">{{ r.capacity }}</td>
          <td class="text-muted small">{{ r.equipment }}</td>
          <td>
            <span class="badge" :class="r.isActive ? 'text-bg-success' : 'text-bg-secondary'">
              {{ r.isActive ? '啟用中' : '已停用' }}
            </span>
          </td>
          <td class="text-end">
            <button class="btn btn-sm btn-outline-primary me-1" @click="openEdit(r)">編輯</button>
            <button class="btn btn-sm" :class="r.isActive ? 'btn-outline-danger' : 'btn-outline-success'"
                    @click="toggleActive(r)">{{ r.isActive ? '停用' : '啟用' }}</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="modal fade" tabindex="-1" ref="dialog">
    <div class="modal-dialog">
      <div class="modal-content" v-if="editing">
        <div class="modal-header">
          <h5 class="modal-title">{{ editing.id ? '編輯會議廳' : '新增會議廳' }}</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
        </div>
        <div class="modal-body">
          <div class="mb-3">
            <label class="form-label">名稱</label>
            <input class="form-control" v-model.trim="editing.name" maxlength="50" />
          </div>
          <div class="mb-3">
            <label class="form-label">位置</label>
            <input class="form-control" v-model.trim="editing.location" maxlength="100" />
          </div>
          <div class="mb-3">
            <label class="form-label">容納人數</label>
            <input type="number" min="1" max="1000" class="form-control" v-model.number="editing.capacity" />
          </div>
          <div class="mb-3">
            <label class="form-label">設備</label>
            <input class="form-control" v-model.trim="editing.equipment" maxlength="200"
                   placeholder="投影機、視訊設備…" />
          </div>
          <div class="form-check">
            <input class="form-check-input" type="checkbox" id="room-active" v-model="editing.isActive" />
            <label class="form-check-label" for="room-active">啟用中</label>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-outline-secondary" data-bs-dismiss="modal">取消</button>
          <button class="btn btn-primary" :disabled="saving" @click="save">儲存</button>
        </div>
      </div>
    </div>
  </div>
</div>`,
  };
})();
